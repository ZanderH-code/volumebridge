using System.Globalization;
using VolumeBridge.Core;
using VolumeBridge.Core.Dsp;
using VolumeBridge.Core.Usb;

namespace VolumeBridge.Audio;

/// <summary>Point-in-time state for the UI and the periodic log line.</summary>
public sealed record EngineStatus(
    bool Running,
    string? StartError,
    bool UsbConnected,
    bool Streaming,
    short Volume,
    bool Mute,
    double InRmsDb,
    double InPeakDb,
    bool OutputOpen,
    string OutputText,
    double BufferMs,
    double DeviceMs,
    double DriftPpm,
    long Underruns,
    long Overruns,
    double MaxLateMs)
{
    public double LatencyMs => BufferMs + DeviceMs;

    public string VolumeText => Volume == SpeakerDevice.VolumeSilence
        ? "-∞ dB"
        : string.Create(CultureInfo.InvariantCulture, $"{Volume / 256.0:0.0} dB");
}

/// <summary>
/// The whole bridge: USB/IP server + emulated "VolumeBridge Speaker" (attached through usbip-win2)
/// → gain from the Windows volume → real speaker. Shared by the GUI and the console mode.
/// </summary>
public sealed class Engine(AppConfig config, Action<string> log) : IDisposable
{
    readonly object _gate = new();
    readonly LevelMeter _meter = new();
    HighResWaiter? _highRes;
    UsbipServer? _server;
    UsbipCli? _cli;
    int? _vhciPort;
    SpeakerDevice? _device;
    AudioBridge? _bridge;
    SpeakerOutput? _output;
    string? _startError;
    bool _disposed;

    public AppConfig Config { get; } = config;

    /// <summary>Starts everything; on failure leaves the engine stopped and <see cref="EngineStatus.StartError"/> set.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_server is not null) return true;
            _startError = null;
            try
            {
                StartCore();
                return true;
            }
            catch (Exception e)
            {
                _startError = e is EngineStartException ? e.Message : Loc.T($"启动失败：{e.Message}", $"Start failed: {e.Message}");
                log("START FAILED: " + e);
                StopCore();
                return false;
            }
        }
    }

    void StartCore()
    {
        string exe = UsbipCli.Locate(Config.UsbipPath)
            ?? throw new EngineStartException(Loc.T(
                "兼容模式需要 usbip-win2 驱动。请重新运行 VolumeBridge 安装包，勾选“安装 usbip-win2 驱动”。",
                "The compatible mode needs the usbip-win2 driver. Run the VolumeBridge installer again and tick \"Install the usbip-win2 driver\"."));
        _cli = new UsbipCli(exe, log);

        var range = VolumeRange.FromDb(Config.MinDb, Config.MaxDb, Config.VolumeStepDb);
        _device = new SpeakerDevice(range);
        _bridge = new AudioBridge(Math.Clamp(Config.TargetBufferMs, 5, 200), Math.Clamp(Config.SmoothingMs, 1, 200));
        var (v0, m0) = _device.VolumeState;
        _bridge.TargetGain = (float)SpeakerDevice.Gain(v0, m0);
        var bridge = _bridge;
        _device.VolumeChanged += (vol, mute) =>
        {
            bridge.TargetGain = (float)SpeakerDevice.Gain(vol, mute);
            log(string.Create(CultureInfo.InvariantCulture,
                $"VOLUME from Windows: {(vol == SpeakerDevice.VolumeSilence ? "-inf" : (vol / 256.0).ToString("0.00", CultureInfo.InvariantCulture))} dB mute={(mute ? 1 : 0)}"));
        };

        _highRes = HighResWaiter.TryCreate();
        if (_highRes is null) log("WARNING: high-resolution timer unavailable; URB pacing will be coarse.");
        IPreciseWaiter waiter = _highRes is not null ? _highRes : new SleepWaiter();

        _server = new UsbipServer(_device, pcm =>
        {
            _meter.Add(pcm, SampleKind.Pcm16);
            bridge.Write(pcm);
        }, waiter, log, Config.UsbipPort);
        try { _server.Start(); }
        catch (Exception e)
        {
            throw new EngineStartException(Loc.T(
                $"无法监听本机端口 {Config.UsbipPort}（{e.Message}）。可能有其他 USB/IP 程序（如 Virtual Cables）占用，可在 config.json 修改 usbipPort。",
                $"Cannot listen on local port {Config.UsbipPort} ({e.Message}). Another USB/IP program (e.g. Virtual Cables) may use it; change usbipPort in config.json."));
        }
        log(string.Create(CultureInfo.InvariantCulture,
            $"USB/IP server on 127.0.0.1:{_server.Port}; volume range {range.Min / 256.0:0.##}..{range.Max / 256.0:0.##} dB, step {range.Res / 256.0:0.###} dB"));

        try
        {
            _cli.DetachStale(_server.Port);
            _vhciPort = _cli.Attach(_server.Port);
        }
        catch (Exception e)
        {
            throw new EngineStartException(Loc.T(
                $"虚拟音箱接入失败：{e.Message}。如果刚安装完驱动，请重启电脑后再试。",
                $"Could not attach the virtual speaker: {e.Message}. If the driver was just installed, restart Windows and try again."));
        }
        log($"Attached as usbip port {_vhciPort}.");

        // Positive ppm = buffer too full = Windows sends faster than the speaker plays: slow the
        // emulated USB clock down. 0 while no output is running.
        var server = _server;
        _output = new SpeakerOutput(Config, bridge, ppm => server.Clock.RateScale = 1 + ppm * 1e-6, log);
        _output.Start();
    }

    /// <summary>Detaches the virtual speaker and stops all threads.</summary>
    public void Stop()
    {
        lock (_gate) StopCore();
    }

    void StopCore()
    {
        _output?.Dispose();
        _output = null;
        if (_vhciPort is int port && _cli is not null)
        {
            log("Stopping: detaching usbip port before closing the server.");
            try { _cli.Detach(port); } catch (Exception e) { log($"detach failed: {e.Message}"); }
        }
        _vhciPort = null;
        _server?.Dispose();
        _server = null;
        _highRes?.Dispose();
        _highRes = null;
        _bridge = null;
        _device = null;
    }

    /// <summary>Re-reads output selection from <see cref="Config"/> and reopens the output device.
    /// Non-blocking: while Start is still running, the output will read the new config when it opens.</summary>
    public void ReopenOutput()
    {
        if (!Monitor.TryEnter(_gate)) return;
        try { _output?.Reopen(); }
        finally { Monitor.Exit(_gate); }
    }

    static EngineStatus Starting => new(false, null, false, false, 0, false,
        double.NegativeInfinity, double.NegativeInfinity, false, Loc.T("启动中", "Starting"), 0, 0, 0, 0, 0, 0);

    /// <summary>Never blocks: while Start/Stop holds the lock (attach can take seconds) it reports "starting".</summary>
    public EngineStatus GetStatus()
    {
        if (!Monitor.TryEnter(_gate)) return Starting;
        try
        {
            var (rms, peak) = _meter.Read();
            var (vol, mute) = _device?.VolumeState ?? ((short)0, false);
            return new EngineStatus(
                Running: _server is not null,
                StartError: _startError,
                UsbConnected: _server?.SessionActive ?? false,
                Streaming: _device?.PlaybackActive ?? false,
                Volume: vol,
                Mute: mute,
                InRmsDb: rms,
                InPeakDb: peak,
                OutputOpen: _bridge?.OutputActive ?? false,
                OutputText: _output?.Status ?? Loc.T("未运行", "Not running"),
                BufferMs: _bridge?.FillMs ?? 0,
                DeviceMs: _output?.PaddingMs ?? 0,
                DriftPpm: _bridge?.Drift.Ppm ?? 0,
                Underruns: _bridge?.Underruns ?? 0,
                Overruns: _bridge?.Overruns ?? 0,
                MaxLateMs: _server?.Stats.TakeMaxLateMs() ?? 0);
        }
        finally { Monitor.Exit(_gate); }
    }

    public static string StatusLine(EngineStatus s) => string.Create(CultureInfo.InvariantCulture,
        $"usb={(s.UsbConnected ? "up" : "DOWN")}/{(s.Streaming ? "streaming" : "idle")} vol={s.VolumeText} mute={(s.Mute ? 1 : 0)} " +
        $"in rms={Db(s.InRmsDb)} peak={Db(s.InPeakDb)} dBFS maxLate={s.MaxLateMs:0.0}ms | out={s.OutputText} " +
        $"buffer={s.BufferMs:0.0}ms device={s.DeviceMs:0.0}ms latency~{s.LatencyMs:0}ms drift={s.DriftPpm:+0;-0}ppm " +
        $"underruns={s.Underruns} overruns={s.Overruns}");

    static string Db(double db) =>
        double.IsNegativeInfinity(db) ? "-inf" : db.ToString("0.0", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            StopCore();
        }
    }
}

public sealed class EngineStartException(string message) : Exception(message);
