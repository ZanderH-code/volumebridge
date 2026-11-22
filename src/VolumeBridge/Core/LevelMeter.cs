namespace VolumeBridge.Core;

public enum SampleKind { Float32, Pcm16, Pcm24, Pcm32 }

/// <summary>Thread-safe running RMS/peak over interleaved sample bytes; Read() returns and resets.</summary>
public sealed class LevelMeter
{
    readonly object _gate = new();
    double _sumSq;
    double _peak;
    long _count;

    public void Add(ReadOnlySpan<byte> data, SampleKind kind)
    {
        double sumSq = 0, peak = 0;
        long n = 0;
        switch (kind)
        {
            case SampleKind.Float32:
                for (int i = 0; i + 4 <= data.Length; i += 4) Acc(BitConverter.ToSingle(data[i..]));
                break;
            case SampleKind.Pcm16:
                for (int i = 0; i + 2 <= data.Length; i += 2) Acc(BitConverter.ToInt16(data[i..]) / 32768.0);
                break;
            case SampleKind.Pcm24:
                for (int i = 0; i + 3 <= data.Length; i += 3)
                    Acc(((data[i] << 8 | data[i + 1] << 16 | data[i + 2] << 24) >> 8) / 8388608.0);
                break;
            case SampleKind.Pcm32:
                for (int i = 0; i + 4 <= data.Length; i += 4) Acc(BitConverter.ToInt32(data[i..]) / 2147483648.0);
                break;
        }
        lock (_gate)
        {
            _sumSq += sumSq;
            _count += n;
            if (peak > _peak) _peak = peak;
        }

        void Acc(double v)
        {
            sumSq += v * v;
            double a = Math.Abs(v);
            if (a > peak) peak = a;
            n++;
        }
    }

    /// <summary>(rms dBFS, peak dBFS) since the last call; -inf when silent or no data.</summary>
    public (double RmsDb, double PeakDb) Read()
    {
        lock (_gate)
        {
            double rms = _count == 0 ? 0 : Math.Sqrt(_sumSq / _count);
            var result = (ToDb(rms), ToDb(_peak));
            _sumSq = 0; _peak = 0; _count = 0;
            return result;
        }
    }

    public static double ToDb(double linear) => linear <= 0 ? double.NegativeInfinity : 20 * Math.Log10(linear);
}
