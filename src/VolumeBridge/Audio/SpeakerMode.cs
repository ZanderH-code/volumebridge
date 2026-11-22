using System.Globalization;
using VolumeBridge.Core;
using VolumeBridge.Core.Usb;

namespace VolumeBridge.Audio;

/// <summary>
/// M1: expose "VolumeBridge Speaker" through usbip-win2 and log what Windows sends to it
/// (volume, mute, audio level, URB timing). Nothing is played on the Flip 6 yet.
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
        device.VolumeChanged += (vol, mute) => log(string.Create(CultureInfo.InvariantCulture,
            $"VOLUME from Windows: {DbText(vol)} mute={(mute ? 1 : 0)} -> gain {SpeakerDevice.Gain(vol, mute):0.0000}"));

        var meter = new LevelMeter();
        using var highRes = HighResWaiter.TryCreate();
        IPreciseWaiter waiter = highRes is not null ? highRes : new SleepWaiter();
        if (highRes is null) log("WARNING: high-resolution timer unavailable; URB pacing will be coarse.");

        using var server = new UsbipServer(device, pcm => meter.Add(pcm, SampleKind.Pcm16), waiter, log, config.UsbipPort);
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
        log($"Attached as usbip port {vhciPort}. Look for \"{SpeakerDescriptors.Product}\" in Sound settings and make it the default output.");

        try
        {
            long lastFrames = 0;
            while (!ct.WaitHandle.WaitOne(1000))
            {
                var (vol, mute) = device.VolumeState;
                var (rms, peak) = meter.Read();
                long frames = server.Stats.Frames;
                log(string.Create(CultureInfo.InvariantCulture,
                    $"session={(server.SessionActive ? "up" : "DOWN")} streaming={(device.PlaybackActive ? 1 : 0)} " +
                    $"vol={DbText(vol)} mute={(mute ? 1 : 0)} level rms={Db(rms)} peak={Db(peak)} dBFS " +
                    $"rate={frames - lastFrames}fps urbs={server.Stats.IsoUrbs} unlinks={server.Stats.Unlinks} maxLate={server.Stats.TakeMaxLateMs():0.0}ms"));
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
