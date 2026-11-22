using System.Diagnostics;

namespace VolumeBridge.Core.Usb;

/// <summary>Blocks until a <see cref="Stopwatch"/> timestamp. Returns false if cancelled first.</summary>
public interface IPreciseWaiter
{
    bool WaitUntil(long timestamp, CancellationToken ct);
}

/// <summary>Portable waiter; precision is the OS timer resolution (≈1 ms on Linux, 15.6 ms on Windows by default).</summary>
public sealed class SleepWaiter : IPreciseWaiter
{
    public bool WaitUntil(long timestamp, CancellationToken ct)
    {
        while (true)
        {
            if (ct.IsCancellationRequested) return false;
            long remaining = timestamp - Stopwatch.GetTimestamp();
            if (remaining <= 0) return true;
            int ms = (int)Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency);
            if (ct.WaitHandle.WaitOne(Math.Max(ms, 1))) return false;
        }
    }
}

/// <summary>
/// The emulated device's clock: isochronous URBs complete one 1 ms frame per packet on a continuous
/// timeline, so a host that queues several URBs still sees real-time pacing
/// (idea from Virtual Cables' reserveIsoCompletion). <see cref="RateScale"/> lets a later PLL
/// stretch the timeline to follow the output device's clock; 1.0 means wall clock.
/// </summary>
public sealed class IsoClock
{
    static readonly long TicksPerMs = Stopwatch.Frequency / 1000;
    static readonly long MaxReservation = 250 * TicksPerMs;

    readonly object _gate = new();
    long _next;

    public double RateScale { get; set; } = 1.0;

    /// <summary>Reserves <paramref name="packets"/> frames after the previous reservation (or after now, if idle).</summary>
    public long Reserve(int packets, long now)
    {
        long duration = Math.Min((long)(packets * TicksPerMs * RateScale), MaxReservation);
        lock (_gate)
        {
            long start = Math.Max(_next, now);
            _next = start + duration;
            return _next;
        }
    }

    public void Reset()
    {
        lock (_gate) _next = 0;
    }
}
