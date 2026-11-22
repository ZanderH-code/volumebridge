using System.Globalization;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VolumeBridge.Core;

namespace VolumeBridge.Audio;

/// <summary>
/// Phase 1 verification tool. Every 200 ms prints the virtual cable's playback endpoint volume/mute,
/// the level actually arriving at its recording endpoint, and the output device's state.
/// Output is mirrored to probe.log next to the exe.
/// </summary>
public sealed class Probe(AppConfig config, TextWriter output)
{
    static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    const int IntervalMs = 200;

    readonly LevelMeter _meter = new();
    volatile WasapiCapture? _capture;
    MMDevice? _captureDevice;
    string? _captureId;
    volatile bool _capturing;
    bool _unsupported;
    DateTime _nextCaptureRetryUtc;
    static readonly TimeSpan CaptureRetryDelay = TimeSpan.FromSeconds(2);
    string? _lastSelection;

    public int Run(TimeSpan? duration, CancellationToken ct)
    {
        using var enumerator = new MMDeviceEnumerator();
        var end = duration is null ? DateTime.MaxValue : DateTime.UtcNow + duration.Value;
        try
        {
            while (!ct.IsCancellationRequested && DateTime.UtcNow < end)
            {
                try { Tick(enumerator); }
                catch (Exception e) { Line($"ERROR {e.GetType().Name}: {e.Message}"); }
                ct.WaitHandle.WaitOne(IntervalMs);
            }
        }
        finally { StopCapture(); }
        return 0;
    }

    void Tick(MMDeviceEnumerator enumerator)
    {
        var renders = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var captures = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        try
        {
            var rInfo = renders.Select(Info).ToList();
            var cInfo = captures.Select(Info).ToList();
            var virt = DeviceMatcher.FindVirtual(config.VirtualCandidates, rInfo, cInfo);
            var outp = DeviceMatcher.FindOutput(config.OutputKeywords, config.OutputDeviceId, rInfo, virt?.Render.Id);

            string selection = string.Join("|", rInfo.Select(d => d.Id)) + "#" + string.Join("|", cInfo.Select(d => d.Id))
                               + "#" + virt?.Candidate.Name + "#" + outp.Device?.Id + outp.Error;
            if (selection != _lastSelection)
            {
                PrintInventory(renders, captures, virt, outp);
                _lastSelection = selection;
            }

            EnsureCapture(enumerator, virt?.Capture.Id);

            var parts = new List<string>();
            if (virt is not null)
            {
                var vr = renders.First(d => d.ID == virt.Render.Id);
                parts.Add($"VIRT {VolumeText(vr)} {FormatText(vr)}");
                var (rms, peak) = _meter.Read();
                parts.Add(!_capturing ? "REC (not capturing)" : $"REC rms={Db(rms)} peak={Db(peak)} dBFS");
            }
            else parts.Add("VIRT none");

            if (outp.Device is null) parts.Add("OUT none");
            else
            {
                var od = renders.First(d => d.ID == outp.Device.Id);
                parts.Add($"OUT {VolumeText(od)} {FormatText(od)}");
            }

            Line(string.Join(" | ", parts));
        }
        finally
        {
            foreach (var d in renders) d.Dispose();
            foreach (var d in captures) d.Dispose();
        }
    }

    void PrintInventory(List<MMDevice> renders, List<MMDevice> captures, VirtualMatch? virt, OutputMatch outp)
    {
        Line("=== device inventory ===");
        Line("Playback (render) endpoints:");
        foreach (var d in renders) Line($"  \"{d.FriendlyName}\"  {FormatText(d)}  hw=[{HwText(d)}]  id={d.ID}");
        Line("Recording (capture) endpoints:");
        foreach (var d in captures) Line($"  \"{d.FriendlyName}\"  {FormatText(d)}  id={d.ID}");

        if (virt is null)
        {
            Line("Virtual cable: NOT FOUND. Tried: " +
                 string.Join("; ", config.VirtualCandidates.Select(c => $"{c.Name} (play~\"{c.RenderKeyword}\", rec~\"{c.CaptureKeyword}\")")));
        }
        else
        {
            var vr = renders.First(d => d.ID == virt.Render.Id);
            bool? hwVol = Safe(() => ((uint)vr.AudioEndpointVolume.HardwareSupport & 1) != 0, (bool?)null);
            Line($"Virtual cable: {virt.Candidate.Name}  play=\"{virt.Render.Name}\"  rec=\"{virt.Capture.Name}\"");
            Line(hwVol is null
                ? "  Playback endpoint hardware-support flags unreadable."
                : hwVol.Value
                ? "  Playback endpoint reports HARDWARE volume: Windows will not attenuate; REC level should stay flat when the slider moves -> gainMode=apply."
                : "  Playback endpoint has NO hardware volume: Windows attenuates in software; REC level should follow the slider -> gainMode=passthrough.");
            Line("  Verify: play steady audio, move the slider 100% -> 50% -> 10% and mute; watch REC rms.");
        }
        Line(outp.Device is null ? $"Output: NOT SELECTED ({outp.Error})" : $"Output: \"{outp.Device.Name}\"");
        Line("========================");
    }

    /// <summary>Keeps a capture stream open on <paramref name="id"/>; restarts it if it stopped or the device changed.</summary>
    void EnsureCapture(MMDeviceEnumerator enumerator, string? id)
    {
        if (id == _captureId && (id is null || _capturing || _unsupported || DateTime.UtcNow < _nextCaptureRetryUtc)) return;
        bool retrySameDevice = id == _captureId;
        StopCapture();
        if (id is null) return;
        if (retrySameDevice) Line("REC restarting capture");
        _captureId = id;
        // Back off between attempts on the same device; a device change retries immediately.
        _nextCaptureRetryUtc = DateTime.UtcNow + CaptureRetryDelay;

        _captureDevice = enumerator.GetDevice(id);
        var cap = new WasapiCapture(_captureDevice, true, 20);
        var kind = KindOf(cap.WaveFormat);
        if (kind is null)
        {
            Line($"REC format unsupported by meter: {cap.WaveFormat}");
            cap.Dispose();
            _unsupported = true;
            return;
        }
        cap.DataAvailable += (_, e) => _meter.Add(e.Buffer.AsSpan(0, e.BytesRecorded), kind.Value);
        cap.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) Line($"REC stopped: {e.Exception.Message}");
            if (ReferenceEquals(_capture, cap)) _capturing = false;
        };
        _meter.Read();
        // Publish before starting: the capture thread may fail and fire RecordingStopped immediately.
        _capture = cap;
        _capturing = true;
        try { cap.StartRecording(); }
        catch (Exception e)
        {
            Line($"REC start failed {Err(e)}: {e.Message}");
            StopCapture();
            _captureId = id; // keep the id so the retry waits out the backoff
        }
    }

    void StopCapture()
    {
        var cap = _capture;
        _capture = null; // our own stop must not look like a failure to RecordingStopped
        if (cap is not null)
        {
            try { cap.StopRecording(); } catch { /* device may already be gone */ }
            cap.Dispose();
        }
        _captureDevice?.Dispose();
        _captureDevice = null;
        _captureId = null;
        _capturing = false;
        _unsupported = false;
    }

    static DeviceInfo Info(MMDevice d) => new(d.ID, d.FriendlyName);

    /// <summary>One misbehaving endpoint (sleeping HDMI/Bluetooth) must not abort the whole report.</summary>
    static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }

    static string Err(Exception e) => $"(err: {e.GetType().Name} 0x{e.HResult:X8})";

    static string VolumeText(MMDevice d)
    {
        try { return VolumeTextUnsafe(d); } catch (Exception e) { return $"\"{Safe(() => d.FriendlyName, "?")}\" {Err(e)}"; }
    }

    static string VolumeTextUnsafe(MMDevice d)
    {
        var v = d.AudioEndpointVolume;
        return string.Create(CultureInfo.InvariantCulture,
            $"\"{d.FriendlyName}\" vol={v.MasterVolumeLevelScalar:0.000} ({v.MasterVolumeLevel:0.0} dB) mute={(v.Mute ? 1 : 0)}");
    }

    static string FormatText(MMDevice d)
    {
        try
        {
            using var client = d.AudioClient;
            var f = client.MixFormat;
            return $"{f.SampleRate}Hz {f.Channels}ch {f.BitsPerSample}{(KindOf(f) == SampleKind.Float32 ? "f" : "i")}";
        }
        catch (Exception e) { return Err(e); }
    }

    static string HwText(MMDevice d)
    {
        try
        {
            uint hw = (uint)d.AudioEndpointVolume.HardwareSupport;
            return $"{((hw & 1) != 0 ? "vol" : "-")},{((hw & 2) != 0 ? "mute" : "-")},{((hw & 4) != 0 ? "meter" : "-")}";
        }
        catch (Exception e) { return Err(e); }
    }

    static SampleKind? KindOf(WaveFormat f)
    {
        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (f is WaveFormatExtensible x && x.SubFormat == IeeeFloat);
        bool isPcm = f.Encoding == WaveFormatEncoding.Pcm || (f is WaveFormatExtensible y && y.SubFormat == Pcm);
        return (isFloat, isPcm, f.BitsPerSample) switch
        {
            (true, _, 32) => SampleKind.Float32,
            (_, true, 16) => SampleKind.Pcm16,
            (_, true, 24) => SampleKind.Pcm24,
            (_, true, 32) => SampleKind.Pcm32,
            _ => null,
        };
    }

    static string Db(double db) =>
        double.IsNegativeInfinity(db) ? "-inf" : db.ToString("0.0", CultureInfo.InvariantCulture);

    void Line(string s)
    {
        lock (output) output.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}");
    }
}
