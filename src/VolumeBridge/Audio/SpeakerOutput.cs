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
    public string Status { get; private set; } = "starting";
    public double PaddingMs { get; private set; }

    public void Start() => _thread.Start();

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(3000);
    }

    void Loop()
    {
        var backoff = TimeSpan.FromMilliseconds(500);
        while (!_stop.IsCancellationRequested)
        {
            var started = Stopwatch.StartNew();
            try { RunOnce(); }
            catch (Exception e) when (e is COMException or InvalidOperationException or TimeoutException or ArgumentException)
            {
                Problem($"output error: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
            }
            _bridge.Stop();
            _onPpm(0);
            PaddingMs = 0;
            if (started.Elapsed > TimeSpan.FromSeconds(10)) backoff = TimeSpan.FromMilliseconds(500);
            if (_stop.Token.WaitHandle.WaitOne(backoff)) return;
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
                Problem($"output not available: {match.Error}");
                return;
            }
            (id, name) = (match.Device.Id, match.Device.Name);
        }
        finally
        {
            foreach (var d in renders) d.Dispose();
        }

        using var device = enumerator.GetDevice(id);
        using var client = device.AudioClient;
        var mix = client.MixFormat;
        var fmt = WaveFormats.ToOutput(mix) ?? throw new InvalidOperationException($"unsupported mix format {mix}");

        long bufferDuration = Math.Clamp(_config.OutputBufferMs, 3, 200) * 10_000L;
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback, bufferDuration, 0, mix, Guid.Empty);
        using var ready = new AutoResetEvent(false);
        client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
        var render = client.AudioRenderClient;
        int bufferFrames = client.BufferSize;

        _bridge.Start(fmt);
        _log(string.Create(CultureInfo.InvariantCulture,
            $"output opened: \"{name}\" {fmt.SampleRate}Hz {fmt.Channels}ch {fmt.Sample}, buffer {bufferFrames * 1000.0 / fmt.SampleRate:0.0} ms, stream latency {client.StreamLatency / 10_000.0:0.0} ms"));
        _lastProblem = null;
        Status = $"\"{name}\"";

        int taskIndex = 0;
        IntPtr mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        var clock = Stopwatch.StartNew();
        double last = 0;
        client.Start();
        try
        {
            var waits = new WaitHandle[] { ready, _stop.Token.WaitHandle };
            while (true)
            {
                int signaled = WaitHandle.WaitAny(waits, 1000);
                if (signaled == 1) return;
                if (signaled == WaitHandle.WaitTimeout) throw new TimeoutException("render event stalled (device asleep or removed?)");

                int padding = client.CurrentPadding;
                int available = bufferFrames - padding;
                PaddingMs = padding * 1000.0 / fmt.SampleRate;
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
            try { client.Stop(); } catch (COMException) { /* device already gone */ }
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
            Status = "closed";
        }
    }

    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref int taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
