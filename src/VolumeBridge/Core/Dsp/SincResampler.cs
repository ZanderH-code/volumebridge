namespace VolumeBridge.Core.Dsp;

/// <summary>
/// Band-limited sample-rate converter: Kaiser-windowed sinc (64 taps, β = 8 ≈ -80 dB stopband,
/// ≈3.8 kHz transition at 48 kHz), polyphase table with 512 phases, linear interpolation between them.
/// The cutoff sits at 96 % of the lower Nyquist frequency: for 48 → 44.1 kHz the passband reaches
/// ≈19 kHz and anything that would alias lands above 20 kHz.
/// Only used when input and output rates differ; equal rates bypass it entirely.
/// Adds Taps/2 input frames of delay (≈0.67 ms at 48 kHz).
/// </summary>
public sealed class SincResampler
{
    public const int Taps = 64;
    const int Phases = 512;
    const double Beta = 8.0;

    readonly int _ch;
    readonly double _step;          // input frames per output frame
    readonly float[] _table;        // (Phases + 1) × Taps
    readonly float[] _hist;         // Taps frames, oldest first
    readonly float[] _frame;
    double _frac;

    public SincResampler(int channels, int inRate, int outRate)
    {
        _ch = channels;
        _step = (double)inRate / outRate;
        _hist = new float[Taps * channels];
        _frame = new float[channels];
        _table = BuildTable(Math.Min(1.0, 1.0 / _step) * 0.96);
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_hist);
        _frac = 0;
    }

    static float[] BuildTable(double cutoff)
    {
        var t = new float[(Phases + 1) * Taps];
        double i0Beta = BesselI0(Beta);
        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases;
            for (int k = 0; k < Taps; k++)
            {
                // Tap k multiplies hist[k]; the output sits frac frames after hist[Taps/2 - 1].
                double x = k - (Taps / 2 - 1) - frac;
                double sinc = x == 0 ? 1.0 : Math.Sin(Math.PI * cutoff * x) / (Math.PI * cutoff * x);
                double w = (x / (Taps / 2.0));
                double kaiser = Math.Abs(w) >= 1 ? 0 : BesselI0(Beta * Math.Sqrt(1 - w * w)) / i0Beta;
                t[p * Taps + k] = (float)(cutoff * sinc * kaiser);
            }
        }
        return t;
    }

    static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 50; k++)
        {
            term *= q / (k * k);
            sum += term;
            if (term < 1e-12 * sum) break;
        }
        return sum;
    }

    /// <summary>Fills up to <paramref name="frames"/> output frames; fewer means <paramref name="src"/> ran dry.</summary>
    public int Process(FloatRing src, Span<float> dst, int frames)
    {
        for (int n = 0; n < frames; n++)
        {
            while (_frac >= 1.0)
            {
                if (!src.TryReadFrame(_frame)) return n;
                Array.Copy(_hist, _ch, _hist, 0, (Taps - 1) * _ch);
                _frame.CopyTo(_hist, (Taps - 1) * _ch);
                _frac -= 1.0;
            }
            double pos = _frac * Phases;
            int p = (int)pos;
            float mix = (float)(pos - p);
            int a = p * Taps, b = a + Taps;
            for (int c = 0; c < _ch; c++)
            {
                float acc = 0;
                for (int k = 0; k < Taps; k++)
                {
                    float h = _table[a + k] + mix * (_table[b + k] - _table[a + k]);
                    acc += h * _hist[k * _ch + c];
                }
                dst[n * _ch + c] = acc;
            }
            _frac += _step;
        }
        return frames;
    }
}
