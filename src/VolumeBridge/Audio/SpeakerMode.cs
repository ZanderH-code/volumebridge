using System.Globalization;
using VolumeBridge.Core;
using VolumeBridge.Core.Dsp;
using VolumeBridge.Core.Usb;

namespace VolumeBridge.Audio;

/// <summary>
/// Main run mode: expose "VolumeBridge Speaker" through usbip-win2, apply the volume/mute Windows
/// sends to it, and play the audio on the real speaker. The emulated USB clock is steered to follow
/// the speaker's clock so the buffer neither drains nor grows.
/// </summary>
public sealed class SpeakerMode(AppConfig config, Action<string> log)
{
    public int Run(CancellationToken ct)
    {
        string? exe = UsbipCli.Locate(config.UsbipPath);
        if (exe is null)
        {
            log("usbip.exe not found. Install usbip-win2 (https://github.com/vadimgrn/usbip-win2/releases) or set usbipPath in config.json.");
            return 3;
        }
        var cli = new UsbipCli(exe, log);

        var range = VolumeRange.FromDb(config.MinDb, config.MaxDb, config.VolumeStepDb);
        var device = new SpeakerDevice(range);
        var bridge = new AudioBridge(Math.Clamp(config.TargetBufferMs, 5, 200), Math.Clamp(config.SmoothingMs, 1, 200));
        var (initialVol, initialMute) = device.VolumeState;
        bridge.TargetGain = (float)SpeakerDevice.Gain(initialVol, initialMute);
        device.VolumeChanged += (vol, mute) =>
        {
            bridge.TargetGain = (float)SpeakerDevice.Gain(vol, mute);
            log(string.Create(CultureInfo.InvariantCulture,
                $"VOLUME from Windows: {DbText(vol)} mute={(mute ? 1 : 0)} -> gain {SpeakerDevice.Gain(vol, mute):0.0000}"));
        };

        var meter = new LevelMeter();
        using var highRes = HighResWaiter.TryCreate();
        IPreciseWaiter waiter = highRes is not null ? highRes : new SleepWaiter();
        if (highRes is null) log("WARNING: high-resolution timer unavailable; URB pacing will be coarse.");

        using var server = new UsbipServer(device, pcm =>
        {
            meter.Add(pcm, SampleKind.Pcm16);
            bridge.Write(pcm);
        }, waiter, log, config.UsbipPort);
        try { server.Start(); }
        catch (Exception e)
        {
            log($"Cannot listen on 127.0.0.1:{config.UsbipPort} ({e.Message}). Another USB/IP server (e.g. Virtual Cables) may use it; change usbipPort.");
            return 4;
        }
        log(string.Create(CultureInfo.InvariantCulture,
            $"USB/IP server on 127.0.0.1:{server.Port}; volume range {range.Min / 256.0:0.##}..{range.Max / 256.0:0.##} dB, step {range.Res / 256.0:0.###} dB"));

        int vhciPort;
        try
        {
            cli.DetachStale(server.Port);
            vhciPort = cli.Attach(server.Port);
        }
        catch (Exception e)
        {
            log($"ATTACH FAILED: {e.Message}");
            return 5;
        }
        log($"Attached as usbip port {vhciPort}. Make \"{SpeakerDescriptors.Product}\" the default output in Sound settings.");

        // Positive ppm = buffer too full = Windows sends faster than the speaker plays: slow the
        // emulated USB clock down (each URB takes slightly longer). 0 while no output is running.
        using var output = new SpeakerOutput(config, bridge, ppm => server.Clock.RateScale = 1 + ppm * 1e-6, log);
        output.Start();

        try
        {
            long lastFrames = 0;
            while (!ct.WaitHandle.WaitOne(1000))
            {
                var (vol, mute) = device.VolumeState;
                var (rms, peak) = meter.Read();
                long frames = server.Stats.Frames;
                double fill = bridge.FillMs;
                log(string.Create(CultureInfo.InvariantCulture,
                    $"usb={(server.SessionActive ? "up" : "DOWN")}/{(device.PlaybackActive ? "streaming" : "idle")} " +
                    $"vol={DbText(vol)} mute={(mute ? 1 : 0)} in rms={Db(rms)} peak={Db(peak)} dBFS {frames - lastFrames}fps maxLate={server.Stats.TakeMaxLateMs():0.0}ms | " +
                    $"out={output.Status} buffer={fill:0.0}ms device={output.PaddingMs:0.0}ms latency~{fill + output.PaddingMs:0}ms " +
                    $"drift={bridge.Drift.Ppm:+0;-0}ppm underruns={bridge.Underruns} overruns={bridge.Overruns}"));
                lastFrames = frames;
            }
        }
        finally
        {
            log("Stopping: detaching usbip port before closing the server.");
            cli.Detach(vhciPort);
        }
        return 0;
    }

    static string DbText(short vol) =>
        vol == SpeakerDevice.VolumeSilence ? "-inf dB" : string.Create(CultureInfo.InvariantCulture, $"{vol / 256.0:0.00} dB");

    static string Db(double db) =>
        double.IsNegativeInfinity(db) ? "-inf" : db.ToString("0.0", CultureInfo.InvariantCulture);
}
