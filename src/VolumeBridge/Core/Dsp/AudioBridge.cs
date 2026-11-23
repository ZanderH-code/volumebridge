using System.Buffers.Binary;

namespace VolumeBridge.Core.Dsp;

public enum SampleFormat { Float32, Int16, Int24, Int32 }

public readonly record struct OutputFormat(int SampleRate, int Channels, SampleFormat Sample)
{
    public int BytesPerSample => Sample switch { SampleFormat.Int16 => 2, SampleFormat.Int24 => 3, _ => 4 };
    public int BytesPerFrame => BytesPerSample * Channels;
}

/// <summary>
/// The audio path between the emulated speaker (48 kHz / 16-bit / stereo in) and the real output:
/// ring buffer → [sinc resampler, only if the output is not 48 kHz] → gain ramp → channel map →
/// output sample format. At 48 kHz the samples pass through unchanged apart from the gain; clock
/// drift is absorbed by steering the emulated USB clock, never by resampling.
/// Also owns buffer-level policy (priming, overrun trimming) and the drift controller whose output
/// steers the emulated USB clock. <see cref="Write"/> runs on the USB/IP thread, <see cref="Render"/>
/// on the output thread.
/// </summary>
public sealed class AudioBridge
{
    public const int InputRate = 48000;
    const int InChannels = 2;
    const double UnderrunMs = 2;
    const double OverrunMarginMs = 40;

    readonly FloatRing _ring;
    SincResampler? _resampler;
    readonly float[] _frame = new float[InChannels];
    readonly double _targetMs;
    readonly int _rampFrames;
    float[] _inScratch = new float[2048];
    float[] _outScratch = new float[2048 * InChannels];

    volatile bool _outputActive;
    OutputFormat _fmt;
    bool _priming = true;
    float _gain;
    volatile float _targetGain;
    float _gainStep;
    float _lastTarget = float.NaN;
    long _underruns, _overruns, _dropped;

    public AudioBridge(double targetMs, double smoothingMs, int capacityMs = 250)
    {
        _targetMs = targetMs;
        _rampFrames = Math.Max(1, (int)(smoothingMs * InputRate / 1000));
        _ring = new FloatRing(InputRate * capacityMs / 1000, InChannels);
        Drift = new DriftController(targetMs);
    }

    public DriftController Drift { get; }
    public double FillMs => _ring.Count * 1000.0 / InputRate;
    public long Underruns => Interlocked.Read(ref _underruns);
    public long Overruns => Interlocked.Read(ref _overruns);
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public bool OutputActive => _outputActive;
    public float TargetGain { get => _targetGain; set => _targetGain = value; }

    /// <summary>Speaker PCM (s16le stereo). Dropped while no output is running.</summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        int samples = pcm.Length / 2;
        if (!_outputActive)
        {
            Interlocked.Add(ref _dropped, samples / InChannels);
            return;
        }
        if (_inScratch.Length < samples) _inScratch = new float[samples];
        for (int i = 0; i < samples; i++) _inScratch[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(2 * i)..]) / 32768f;
        int written = _ring.Write(_inScratch.AsSpan(0, samples));
        Interlocked.Add(ref _dropped, samples / InChannels - written);
    }

    /// <summary>Output (re)opened with <paramref name="fmt"/>; start buffering from scratch.</summary>
    public void Start(OutputFormat fmt)
    {
        _fmt = fmt;
        _ring.Clear();
        _resampler = fmt.SampleRate == InputRate ? null : new SincResampler(InChannels, InputRate, fmt.SampleRate);
        Drift.Reset();
        _priming = true;
        _gain = _targetGain;
        _lastTarget = _targetGain;
        _gainStep = 0;
        _outputActive = true;
    }

    public void Stop()
    {
        _outputActive = false;
        _ring.Clear();
        Drift.Reset();
    }

    /// <summary>
    /// Produces <paramref name="frames"/> output frames into <paramref name="dst"/> and updates the
    /// drift controller (<paramref name="dtSeconds"/> since the previous call). Returns the ppm correction.
    /// </summary>
    public double Render(Span<byte> dst, int frames, double dtSeconds)
    {
        var fmt = _fmt;
        double fill = FillMs;

        if (!_priming && fill < UnderrunMs)
        {
            _priming = true;
            Interlocked.Increment(ref _underruns);
        }
        if (_priming && fill >= _targetMs)
        {
            _priming = false;
            _resampler?.Reset();
        }
        if (fill > _targetMs + OverrunMarginMs)
        {
            _ring.Discard((int)((fill - _targetMs) * InputRate / 1000));
            Interlocked.Increment(ref _overruns);
        }

        if (_outScratch.Length < frames * InChannels) _outScratch = new float[frames * InChannels];
        var buf = _outScratch.AsSpan(0, frames * InChannels);
        int produced = _priming ? 0 : _resampler?.Process(_ring, buf, frames) ?? ReadDirect(buf, frames);
        buf[(produced * InChannels)..].Clear();

        ApplyGain(buf, frames);
        Pack(buf, dst, frames, fmt);

        return _priming ? Drift.Ppm : Drift.Update(FillMs, dtSeconds);
    }

    int ReadDirect(Span<float> buf, int frames)
    {
        for (int n = 0; n < frames; n++)
        {
            if (!_ring.TryReadFrame(_frame)) return n;
            buf[2 * n] = _frame[0];
            buf[2 * n + 1] = _frame[1];
        }
        return frames;
    }

    void ApplyGain(Span<float> buf, int frames)
    {
        float target = _targetGain;
        if (target != _lastTarget)
        {
            _lastTarget = target;
            _gainStep = (target - _gain) / _rampFrames;
        }
        for (int f = 0; f < frames; f++)
        {
            if (_gainStep != 0)
            {
                _gain += _gainStep;
                if ((_gainStep > 0 && _gain >= target) || (_gainStep < 0 && _gain <= target))
                {
                    _gain = target;
                    _gainStep = 0;
                }
            }
            buf[2 * f] *= _gain;
            buf[2 * f + 1] *= _gain;
        }
    }

    /// <summary>Stereo float → output channel layout and sample format.</summary>
    static void Pack(ReadOnlySpan<float> stereo, Span<byte> dst, int frames, OutputFormat fmt)
    {
        int bps = fmt.BytesPerSample;
        dst[..(frames * fmt.BytesPerFrame)].Clear();
        for (int f = 0; f < frames; f++)
        {
            float l = stereo[2 * f], r = stereo[2 * f + 1];
            int o = f * fmt.BytesPerFrame;
            if (fmt.Channels == 1)
            {
                Put(dst[o..], 0.5f * (l + r), fmt.Sample);
                continue;
            }
            Put(dst[o..], l, fmt.Sample);          // further channels (if any) stay silent
            Put(dst[(o + bps)..], r, fmt.Sample);
        }
    }

    static void Put(Span<byte> d, float v, SampleFormat s)
    {
        v = Math.Clamp(v, -1f, 1f);
        switch (s)
        {
            case SampleFormat.Float32: BinaryPrimitives.WriteSingleLittleEndian(d, v); break;
            case SampleFormat.Int16: BinaryPrimitives.WriteInt16LittleEndian(d, (short)Math.Round(v * 32767f)); break;
            case SampleFormat.Int24:
                int x = (int)Math.Round(v * 8388607f);
                d[0] = (byte)x; d[1] = (byte)(x >> 8); d[2] = (byte)(x >> 16);
                break;
            case SampleFormat.Int32: BinaryPrimitives.WriteInt32LittleEndian(d, (int)Math.Clamp(Math.Round(v * 2147483647.0), int.MinValue, int.MaxValue)); break;
        }
    }
}
