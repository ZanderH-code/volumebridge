using System.Buffers.Binary;
using VolumeBridge.Core.Dsp;
using Xunit;

namespace VolumeBridge.Tests;

public class SincResamplerTests
{
    static FloatRing Sine(double freq, int rate, int frames)
    {
        var ring = new FloatRing(frames, 2);
        var buf = new float[frames * 2];
        for (int i = 0; i < frames; i++)
            buf[2 * i] = buf[2 * i + 1] = (float)(0.5 * Math.Sin(2 * Math.PI * freq * i / rate));
        ring.Write(buf);
        return ring;
    }

    /// <summary>Amplitude of <paramref name="freq"/> in the signal (single-bin DFT), after skipping the warm-up.</summary>
    static double Amplitude(float[] x, int frames, double freq, int rate, int skip)
    {
        double re = 0, im = 0;
        int n = frames - skip;
        for (int i = skip; i < frames; i++)
        {
            re += x[2 * i] * Math.Cos(2 * Math.PI * freq * i / rate);
            im += x[2 * i] * Math.Sin(2 * Math.PI * freq * i / rate);
        }
        return 2 * Math.Sqrt(re * re + im * im) / n;
    }

    [Fact]
    public void Downsample48To441_KeepsAudibleToneAndRemovesAliasingBand()
    {
        var r = new SincResampler(2, 48000, 44100);
        var outBuf = new float[44100 * 2];
        int got = r.Process(Sine(1000, 48000, 48000), outBuf, 40000);
        Assert.Equal(40000, got);
        Assert.InRange(Amplitude(outBuf, got, 1000, 44100, 1000), 0.49, 0.51);

        // High treble survives (18 kHz within 0.1 dB of 0.5).
        var r3 = new SincResampler(2, 48000, 44100);
        var treble = new float[44100 * 2];
        r3.Process(Sine(18000, 48000, 48000), treble, 40000);
        Assert.InRange(Amplitude(treble, 40000, 18000, 44100, 1000), 0.494, 0.506);

        // 23.5 kHz is above the new Nyquist (22.05 kHz): it must be suppressed (≥ 60 dB), not folded to 20.6 kHz.
        var r2 = new SincResampler(2, 48000, 44100);
        var alias = new float[44100 * 2];
        r2.Process(Sine(23500, 48000, 48000), alias, 40000);
        Assert.True(Amplitude(alias, 40000, 44100 - 23500, 44100, 1000) < 0.0005);
    }
}

public class DriftControllerTests
{
    /// <summary>Producer clock steered by the controller vs. a consumer whose clock runs 200 ppm fast.</summary>
    [Fact]
    public void ConvergesToTarget_WithinClampedPpm()
    {
        var d = new DriftController(20);
        double fill = 20, ppm = 0;
        const double consumerDrift = 200e-6;
        const double dt = 0.01;
        for (int i = 0; i < 60 * 100; i++)
        {
            double produced = dt * 1000 / (1 + ppm * 1e-6);   // ms of audio per period (steered clock)
            double consumed = dt * 1000 * (1 + consumerDrift);
            fill += produced - consumed;
            double sawtooth = (i % 2 == 0) ? 5 : -5;           // WASAPI period jitter
            ppm = d.Update(fill + sawtooth, dt);
        }
        Assert.InRange(fill, 18, 22);
        Assert.InRange(ppm, -260, -140); // producer must run ~200 ppm faster
    }
}

public class AudioBridgeTests
{
    static byte[] Pcm16(params short[] samples)
    {
        var b = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(2 * i), samples[i]);
        return b;
    }

    [Fact]
    public void At48k_PassesSamplesThroughUnchanged_AfterPriming()
    {
        var bridge = new AudioBridge(targetMs: 1, smoothingMs: 1) { TargetGain = 1f };
        bridge.Start(new OutputFormat(48000, 2, SampleFormat.Int16));
        var input = Enumerable.Range(0, 96 * 2).Select(i => (short)(i * 100 - 9000))
            .Concat(new short[] { -32768, 32767, -16384, 16384, -1, 1, 0, 0 }).ToArray();
        bridge.Write(Pcm16(input));
        bridge.Write(Pcm16(new short[96 * 2])); // keep enough queued to leave priming

        var dst = new byte[input.Length * 2];
        bridge.Render(dst, input.Length / 2, 0.002);
        for (int i = 0; i < input.Length; i++)
            Assert.Equal(input[i], BinaryPrimitives.ReadInt16LittleEndian(dst.AsSpan(2 * i)));
    }

    [Fact]
    public void GainChange_RampsSmoothly_AndMuteReachesSilence()
    {
        var bridge = new AudioBridge(targetMs: 1, smoothingMs: 1) { TargetGain = 1f };   // 48-frame ramp
        bridge.Start(new OutputFormat(48000, 1, SampleFormat.Float32));
        bridge.Write(Pcm16(Enumerable.Repeat((short)16384, 400 * 2).ToArray()));
        bridge.TargetGain = 0f;

        var dst = new byte[200 * 4];
        bridge.Render(dst, 200, 0.004);
        var v = Enumerable.Range(0, 200).Select(i => BinaryPrimitives.ReadSingleLittleEndian(dst.AsSpan(4 * i))).ToArray();

        for (int i = 1; i < 200; i++) Assert.True(v[i] <= v[i - 1] + 1e-6f, $"not monotonic at {i}");
        Assert.True(v[0] > 0.45f);           // starts near full level (0.5)
        Assert.True(v[10] > 0.3f);           // no instant jump
        Assert.Equal(0f, v[60]);             // fully muted after the ramp
    }

    [Fact]
    public void Underrun_OutputsSilence_ThenReprimesToTarget()
    {
        var bridge = new AudioBridge(targetMs: 2, smoothingMs: 1) { TargetGain = 1f };
        bridge.Start(new OutputFormat(48000, 2, SampleFormat.Float32));
        bridge.Write(Pcm16(Enumerable.Repeat((short)8000, 192 * 2).ToArray()));  // 4 ms = target + one period
        var dst = new byte[96 * 8];
        bridge.Render(dst, 96, 0.002);                       // primes and plays 2 ms
        bridge.Render(dst, 96, 0.002);                       // plays the last 2 ms
        bridge.Render(dst, 96, 0.002);                       // empty -> underrun
        Assert.Equal(1, bridge.Underruns);
        Assert.All(Enumerable.Range(0, 96 * 2), i => Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(dst.AsSpan(4 * i))));
    }

    [Fact]
    public void WhileStopped_InputIsDroppedNotBuffered()
    {
        var bridge = new AudioBridge(targetMs: 20, smoothingMs: 20);
        bridge.Write(Pcm16(new short[960]));
        Assert.Equal(0, bridge.FillMs);
        Assert.Equal(480, bridge.DroppedFrames);
    }
}

public class FloatRingTests
{
    [Fact]
    public void BulkRead_WrapsAround()
    {
        var r = new FloatRing(4, 1);
        r.Write([1, 2, 3]);
        r.Discard(2);
        r.Write([4, 5, 6]);           // wraps
        var dst = new float[5];
        Assert.Equal(4, r.Read(dst));
        Assert.Equal(new float[] { 3, 4, 5, 6, 0 }, dst);
    }
}

public class AudioBridgeUnderrunTests
{
    [Fact]
    public void PartialRead_CountsUnderrun_AndReprimes()
    {
        var bridge = new AudioBridge(targetMs: 1, smoothingMs: 1) { TargetGain = 1f };
        bridge.Start(new OutputFormat(48000, 2, SampleFormat.Float32));
        var pcm = new byte[150 * 4];
        bridge.Write(pcm);                                   // 150 frames ≥ 48 target + 96 period
        var dst = new byte[96 * 8];
        bridge.Render(dst, 96, 0.002);                       // 54 left
        bridge.Render(dst, 96, 0.002);                       // only 54 of 96 available
        Assert.Equal(1, bridge.Underruns);
    }
}
