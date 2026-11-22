using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VolumeBridge.Core.Usb;

namespace VolumeBridge.Audio;

/// <summary>
/// Sub-millisecond waits via a high-resolution waitable timer (Windows 10 1803+). Unlike timeBeginPeriod,
/// it keeps working for background processes on Windows 11. One instance per waiting thread.
/// </summary>
public sealed partial class HighResWaiter : IPreciseWaiter, IDisposable
{
    const uint CreateWaitableTimerHighResolution = 0x00000002;
    const uint TimerAllAccess = 0x1F0003;

    readonly TimerHandle _timer;

    HighResWaiter(SafeWaitHandle h) => _timer = new TimerHandle(h);

    /// <summary>Null when the OS does not support high-resolution timers.</summary>
    public static HighResWaiter? TryCreate()
    {
        var h = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return new HighResWaiter(h);
    }

    public bool WaitUntil(long timestamp, CancellationToken ct)
    {
        long remaining = timestamp - Stopwatch.GetTimestamp();
        if (remaining <= 0) return !ct.IsCancellationRequested;
        long due100ns = -Math.Max(1, remaining * 10_000_000 / Stopwatch.Frequency); // negative = relative
        lock (_timer)
        {
            if (!SetWaitableTimer(_timer.SafeWaitHandle, ref due100ns, 0, IntPtr.Zero, IntPtr.Zero, false))
                throw new InvalidOperationException($"SetWaitableTimer failed: {Marshal.GetLastPInvokeError()}");
            return WaitHandle.WaitAny([_timer, ct.WaitHandle]) == 0;
        }
    }

    public void Dispose() => _timer.Dispose();

    sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(SafeWaitHandle h) => SafeWaitHandle = h;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr arg,
        [MarshalAs(UnmanagedType.Bool)] bool resume);
}
