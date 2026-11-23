using System.Globalization;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using VolumeBridge.Core;
using VolumeBridge.Core.Usb;
using static VolumeBridge.Core.Loc;

namespace VolumeBridge.Audio;

public enum ApoState
{
    NoSpeaker,          // no matching playback device connected
    NotInstalled,       // APO not registered for this speaker (or unsigned APOs blocked)
    EnhancementsOff,    // "Audio enhancements" = Off: Windows skips effects
    Idle,               // registered, nothing playing right now (APO not running)
    Working,            // APO is processing audio
}

public sealed record ApoStatus(
    ApoState State,
    string? SpeakerName,
    string? SpeakerId,
    float SliderScalar,
    bool Muted,
    ApoReport? Report)
{
    public string VolumeText => Muted ? T("静音", "Muted")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(SliderScalar * 100)}%  ({(SliderScalar <= 0.001f ? "-∞" : (20 * Math.Log10(SliderScalar)).ToString("0.0", CultureInfo.InvariantCulture))} dB)");

    public string SourceText => Report?.Source switch
    {
        ApoSource.Notification => T("Windows 音量通知", "Windows volume notifications"),
        ApoSource.App => T("VolumeBridge 托盘程序", "VolumeBridge tray app"),
        _ => T("尚未获取", "not yet known"),
    };
}

/// <summary>
/// Zero-latency mode: the speaker itself is the default output and VolumeBridgeApo (in its effect chain)
/// applies the slider/mute. This controller finds the speaker, mirrors its slider into the shared file
/// (the APO's fallback source), and reports state for the UI.
/// </summary>
public sealed class ApoController : IDisposable
{
    readonly AppConfig _config;
    readonly Action<string> _log;
    readonly ApoShared? _shared;
    readonly Thread _thread;
    readonly CancellationTokenSource _stop = new();
    readonly AutoResetEvent _recheck = new(false);
    readonly object _gate = new();
    MMDeviceEnumerator? _enumerator;
    MMDevice? _device;
    ApoStatus _status = new(ApoState.NoSpeaker, null, null, 1, false, null);
    float _lastWritten = -1;

    public ApoController(AppConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        try { _shared = new ApoShared(); }
        catch (Exception e) { log($"cannot open shared APO state ({ApoShared.FilePath}): {e.Message}"); }
        _thread = new Thread(Loop) { IsBackground = true, Name = "apo-controller" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public AppConfig Config => _config;

    public void Start()
    {
        ApplyCap();
        _thread.Start();
    }

    /// <summary>Publishes the configured output ceiling to the plugin.</summary>
    public void ApplyCap() => _shared?.WriteCap(_config.OutputCapGain);

    /// <summary>Re-reads the output selection right away.</summary>
    public void Recheck() => _recheck.Set();

    public ApoStatus GetStatus()
    {
        lock (_gate) return _status;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(3000);
        _shared?.Dispose();
    }

    void Loop()
    {
        _enumerator = new MMDeviceEnumerator();
        string? lastProblem = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try { Refresh(); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    string msg = $"{e.GetType().Name} 0x{e.HResult:X8} {e.Message}";
                    if (msg != lastProblem) _log("APO controller: " + msg);
                    lastProblem = msg;
                    Unsubscribe();
                }
                WaitHandle.WaitAny([_stop.Token.WaitHandle, _recheck], 1000);
            }
        }
        finally
        {
            Unsubscribe();
            _enumerator.Dispose();
        }
    }

    void Refresh()
    {
        var renders = _enumerator!.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        DeviceInfo? target;
        try
        {
            var infos = renders.Select(d => new DeviceInfo(d.ID, d.FriendlyName)).ToList();
            string? virtualId = infos.FirstOrDefault(d => d.Name.Contains(SpeakerDescriptors.Product, StringComparison.OrdinalIgnoreCase))?.Id;
            target = DeviceMatcher.FindOutput(_config.OutputKeywords, _config.OutputDeviceId, infos, virtualId).Device;
        }
        finally { foreach (var d in renders) d.Dispose(); }

        if (target is null)
        {
            Unsubscribe();
            Set(new ApoStatus(ApoState.NoSpeaker, null, null, 1, false, _shared?.Read()));
            return;
        }

        if (_device?.ID != target.Id)
        {
            Unsubscribe();
            _device = _enumerator.GetDevice(target.Id);
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolume;
            _log($"zero-latency mode: speaker \"{target.Name}\" ({target.Id})");
            _lastWritten = -1;
        }

        var vol = _device.AudioEndpointVolume;
        float scalar = vol.MasterVolumeLevelScalar;
        bool muted = vol.Mute;
        Publish(scalar, muted); // also covers missed notifications

        var report = _shared?.Read();
        bool registered = ApoRegistration.IsRegistered(target.Id) && ApoRegistration.UnsignedApoAllowed() && ApoRegistration.ComRegistered();
        ApoState state = !registered ? ApoState.NotInstalled
            : ApoRegistration.EnhancementsDisabled(target.Id) ? ApoState.EnhancementsOff
            : report is { Instances: > 0, HeartbeatAgeMs: < 3000 } ? ApoState.Working
            : ApoState.Idle;
        Set(new ApoStatus(state, target.Name, target.Id, scalar, muted, report));
    }

    void OnVolume(AudioVolumeNotificationData data) => Publish(data.MasterVolume, data.Muted);

    void Publish(float scalar, bool muted)
    {
        float gain = muted ? 0f : Math.Clamp(scalar, 0f, 1f);
        lock (_gate) // controller thread and COM notification thread
        {
            if (gain == _lastWritten) return;
            _lastWritten = gain;
            _shared?.WriteGain(gain);
        }
    }

    void Unsubscribe()
    {
        if (_device is null) return;
        try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume; } catch (Exception) { }
        _device.Dispose();
        _device = null;
    }

    void Set(ApoStatus s)
    {
        lock (_gate) _status = s;
    }

    public static string StatusLine(ApoStatus s) => string.Create(CultureInfo.InvariantCulture,
        $"apo state={s.State} speaker=\"{s.SpeakerName}\" slider={s.SliderScalar:0.000} mute={(s.Muted ? 1 : 0)} " +
        $"instances={s.Report?.Instances} source={s.Report?.Source} gain={s.Report?.TargetGain:0.000} init={s.Report?.InitKind} " +
        $"notifs={s.Report?.Notifications} beatAge={(s.Report?.HeartbeatAgeMs is long a && a < long.MaxValue ? a : -1)}ms rate={s.Report?.SampleRate} ch={s.Report?.Channels}");
}
