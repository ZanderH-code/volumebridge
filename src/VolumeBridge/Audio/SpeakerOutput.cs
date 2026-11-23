using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using VolumeBridge.Core;
using VolumeBridge.Core.Dsp;
using VolumeBridge.Core.Usb;

namespace VolumeBridge.Audio;

/// <summary>
/// Plays the bridge's audio on the real speaker with WASAPI shared mode, event driven, using
/// NAudio's low-level CoreAudioApi only. Re-finds and re-opens the device with backoff when it is
/// missing, unplugged, invalidated or stalls (e.g. after sleep).
/// </summary>
public sealed partial class SpeakerOutput : IDisposable
{
    readonly AppConfig _config;
    readonly AudioBridge _bridge;
    readonly Action<double> _onPpm;
    readonly Action<string> _log;
    readonly CancellationTokenSource _stop = new();
    readonly AutoResetEvent _reopen = new(false);
    volatile bool _reopenRequested;
    readonly Thread _thread;
    string? _lastProblem;

    public SpeakerOutput(AppConfig config, AudioBridge bridge, Action<double> onPpm, Action<string> log)
    {
        _config = config;
        _bridge = bridge;
        _onPpm = onPpm;
        _log = log;
        _thread = new Thread(Loop) { IsBackground = true, Name = "speaker-output" };
    }

    /// <summary>Short status for the periodic log line.</summary>
    public string Status { get; private set; } = Loc.T("启动中", "Starting");
    public double PaddingMs { get; private set; }

    public void Start() => _thread.Start();

    /// <summary>Closes the current device (if any) and looks for the output again right away.</summary>
    public void Reopen()
    {
        _reopenRequested = true;
        _reopen.Set();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(3000);
    }

    /// <summary>Plain-language text for the errors users actually meet.</summary>
    static string Explain(Exception e) => (uint)e.HResult switch
    {
        0x8889000A => Loc.T("被其他程序独占（例如 Voicemeeter），请退出该程序",       // AUDCLNT_E_DEVICE_IN_USE
                            "in use by another program (e.g. Voicemeeter); please quit it"),
        0x88890004 => Loc.T("设备已断开", "device disconnected"),                    // AUDCLNT_E_DEVICE_INVALIDATED
        0x80070005 => Loc.T("暂时无法访问（锁屏、睡眠或音频服务重启中）",              // E_ACCESSDENIED
                            "temporarily unavailable (locked, asleep or audio service restarting)"),
        0x80070490 or 0x80070002 => Loc.T("设备已不存在", "device no longer exists"), // ELEMENT_NOT_FOUND / FILE_NOT_FOUND
        _ => $"{e.GetType().Name} 0x{e.HResult:X8} {e.Message}",
    };

    void Loop()
    {
        var backoff = TimeSpan.FromMilliseconds(500);
        while (!_stop.IsCancellationRequested)
        {
            var started = Stopwatch.StartNew();
            try { RunOnce(); }
            // NAudio maps HRESULTs to many exception types (UnauthorizedAccess on session lock/sleep,
            // FileNotFound when the device vanished, ...); none may kill this thread.
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Problem(Loc.T($"输出错误：{Explain(e)}", $"Output error: {Explain(e)}"));
            }
            _bridge.Stop();
            _onPpm(0);
            PaddingMs = 0;
            if (started.Elapsed > TimeSpan.FromSeconds(10)) backoff = TimeSpan.FromMilliseconds(500);
            if (_reopenRequested)
            {
                _reopenRequested = false;
                continue; // user picked another device: look again immediately
            }
            int woke = WaitHandle.WaitAny([_stop.Token.WaitHandle, _reopen], backoff);
            if (woke == 0) return;
            if (woke == 1)
            {
                _reopenRequested = false;
                backoff = TimeSpan.FromMilliseconds(250);
            }
            backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 5000));
        }
    }

    void Problem(string message)
    {
        Status = message;
        if (message != _lastProblem) _log(message);
        _lastProblem = message;
    }

    unsafe void RunOnce()
    {
        using var enumerator = new MMDeviceEnumerator();
        var renders = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        string id;
        string name;
        try
        {
            var infos = renders.Select(d => new DeviceInfo(d.ID, d.FriendlyName)).ToList();
            string? virtualId = infos.FirstOrDefault(d => d.Name.Contains(SpeakerDescriptors.Product, StringComparison.OrdinalIgnoreCase))?.Id;
            var match = DeviceMatcher.FindOutput(_config.OutputKeywords, _config.OutputDeviceId, infos, virtualId);
            if (match.Device is null)
            {
                Problem(Loc.T($"找不到输出设备：{match.Error}", $"Output device not found: {match.Error}"));
                return;
            }
            (id, name) = (match.Device.Id, match.Device.Name);
        }
        finally
        {
            foreach (var d in renders) d.Dispose();
        }

        // The event must outlive the IAudioClient (the engine signals it while releasing the stream),
        // so it is declared first and disposed last.
        using var ready = new AutoResetEvent(false);
        using var device = enumerator.GetDevice(id);
        using var client = device.AudioClient;
        var mix = client.MixFormat;
        var fmt = WaveFormats.ToOutput(mix) ?? throw new InvalidOperationException($"unsupported mix format {mix}");

        long bufferDuration = Math.Clamp(_config.OutputBufferMs, 3, 200) * 10_000L;
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback, bufferDuration, 0, mix, Guid.Empty);
        client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
        var render = client.AudioRenderClient;
        int bufferFrames = client.BufferSize;

        _bridge.Start(fmt);
        _log(string.Create(CultureInfo.InvariantCulture,
            $"output opened: \"{name}\" {fmt.SampleRate}Hz {fmt.Channels}ch {fmt.Sample}, buffer {bufferFrames * 1000.0 / fmt.SampleRate:0.0} ms, stream latency {client.StreamLatency / 10_000.0:0.0} ms"));
        _lastProblem = null;
        Status = string.Create(CultureInfo.InvariantCulture, $"{name} ({fmt.SampleRate} Hz)");

        int taskIndex = 0;
        IntPtr mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        var clock = Stopwatch.StartNew();
        double last = 0;
        try
        {
            // Pre-fill one buffer of silence so the stream starts without a glitch.
            render.GetBuffer(bufferFrames);
            render.ReleaseBuffer(bufferFrames, AudioClientBufferFlags.Silent);
            client.Start();
            var waits = new WaitHandle[] { ready, _stop.Token.WaitHandle, _reopen };
            while (true)
            {
                int signaled = WaitHandle.WaitAny(waits, 1000);
                if (signaled is 1 or 2) return;
                if (signaled == WaitHandle.WaitTimeout) throw new TimeoutException("render event stalled (device asleep or removed?)");

                int padding = client.CurrentPadding;
                int available = bufferFrames - padding;
                PaddingMs = (padding + available * 0.5) * 1000.0 / fmt.SampleRate; // average device-buffer fill
                if (available <= 0) continue;

                IntPtr ptr = render.GetBuffer(available);
                double now = clock.Elapsed.TotalSeconds;
                double ppm = _bridge.Render(new Span<byte>((void*)ptr, available * fmt.BytesPerFrame), available, now - last);
                last = now;
                render.ReleaseBuffer(available, AudioClientBufferFlags.None);
                _onPpm(ppm);
            }
        }
        finally
        {
            try { client.Stop(); } catch (Exception) { /* device already gone */ }
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
            Status = Loc.T("已关闭", "Closed");
        }
    }

    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref int taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
