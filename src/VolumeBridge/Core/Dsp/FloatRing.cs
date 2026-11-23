namespace VolumeBridge.Core.Dsp;

/// <summary>Fixed-capacity ring of interleaved float frames. Thread-safe (one writer, one reader).</summary>
public sealed class FloatRing(int capacityFrames, int channels)
{
    readonly float[] _buf = new float[capacityFrames * channels];
    readonly object _gate = new();
    int _readFrame;
    int _count;

    public int Channels { get; } = channels;
    public int CapacityFrames { get; } = capacityFrames;
    public int Count { get { lock (_gate) return _count; } }

    /// <summary>Appends frames; frames that do not fit are dropped. Returns frames written.</summary>
    public int Write(ReadOnlySpan<float> interleaved)
    {
        lock (_gate)
        {
            int frames = Math.Min(interleaved.Length / Channels, CapacityFrames - _count);
            int writeFrame = (_readFrame + _count) % CapacityFrames;
            for (int f = 0; f < frames; f++)
            {
                interleaved.Slice(f * Channels, Channels).CopyTo(_buf.AsSpan(writeFrame * Channels, Channels));
                if (++writeFrame == CapacityFrames) writeFrame = 0;
            }
            _count += frames;
            return frames;
        }
    }

    /// <summary>Removes one frame into <paramref name="frame"/>; false if empty.</summary>
    public bool TryReadFrame(Span<float> frame)
    {
        lock (_gate)
        {
            if (_count == 0) return false;
            _buf.AsSpan(_readFrame * Channels, Channels).CopyTo(frame);
            if (++_readFrame == CapacityFrames) _readFrame = 0;
            _count--;
            return true;
        }
    }

    /// <summary>Drops up to <paramref name="frames"/> oldest frames.</summary>
    public void Discard(int frames)
    {
        lock (_gate)
        {
            frames = Math.Clamp(frames, 0, _count);
            _readFrame = (_readFrame + frames) % CapacityFrames;
            _count -= frames;
        }
    }

    public void Clear()
    {
        lock (_gate) { _readFrame = 0; _count = 0; }
    }
}
