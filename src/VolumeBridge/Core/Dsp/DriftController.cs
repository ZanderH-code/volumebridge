namespace VolumeBridge.Core.Dsp;

/// <summary>
/// Keeps the input buffer near its target by steering the emulated USB clock.
/// Input: buffer fill (ms) measured on every render period. Output: a rate correction in ppm,
/// positive = the buffer is too full, so the virtual device should run slower (URBs take longer).
/// The fill is low-pass filtered first (WASAPI periods make it a ±10 ms sawtooth); PI with
/// anti-windup; clamped to ±<see cref="MaxPpm"/>.
/// </summary>
public sealed class DriftController(double targetMs)
{
    public const double MaxPpm = 500;
    const double FilterTauSeconds = 2.0;
    // Plant: 1 ppm moves the buffer by 0.001 ms/s, so Kp = 100 gives a ~10 s loop time constant.
    const double Kp = 100.0;  // ppm per ms of error
    const double Ki = 5.0;    // ppm per (ms·s) of accumulated error

    double _filtered = double.NaN;
    double _integral;

    public double TargetMs { get; } = targetMs;
    public double FilteredMs => double.IsNaN(_filtered) ? 0 : _filtered;
    public double Ppm { get; private set; }

    public void Reset()
    {
        _filtered = double.NaN;
        _integral = 0;
        Ppm = 0;
    }

    public double Update(double fillMs, double dtSeconds)
    {
        if (double.IsNaN(_filtered)) _filtered = fillMs;
        else _filtered += (fillMs - _filtered) * Math.Min(1.0, dtSeconds / FilterTauSeconds);

        double error = _filtered - TargetMs;
        double candidateIntegral = _integral + error * dtSeconds;
        double output = Kp * error + Ki * candidateIntegral;
        if (Math.Abs(output) <= MaxPpm) _integral = candidateIntegral; // anti-windup: freeze while saturated
        Ppm = Math.Clamp(Kp * error + Ki * _integral, -MaxPpm, MaxPpm);
        return Ppm;
    }
}
