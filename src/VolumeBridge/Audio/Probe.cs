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
    const int IntervalMs = 200;

    readonly LevelMeter _meter = new();
    WasapiCapture? _capture;
    MMDevice? _captureDevice;
    string? _captureId;
    volatile bool _capturing;
    bool _unsupported;
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
                _lastSelection = selection;
                PrintInventory(renders, captures, virt, outp);
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
            bool hwVol = ((uint)vr.AudioEndpointVolume.HardwareSupport & 1) != 0;
            Line($"Virtual cable: {virt.Candidate.Name}  play=\"{virt.Render.Name}\"  rec=\"{virt.Capture.Name}\"");
            Line(hwVol
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
        if (id == _captureId && (id is null || _capturing || _unsupported)) return;
        StopCapture();
        if (id is null) return;
        _captureId = id;

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
            _capturing = false;
        };
        _meter.Read();
        cap.StartRecording();
        _capture = cap;
        _capturing = true;
    }

    void StopCapture()
    {
        if (_capture is not null)
        {
            try { _capture.StopRecording(); } catch { /* device may already be gone */ }
            _capture.Dispose();
        }
        _captureDevice?.Dispose();
        _capture = null;
        _captureDevice = null;
        _captureId = null;
        _capturing = false;
        _unsupported = false;
    }

    static DeviceInfo Info(MMDevice d) => new(d.ID, d.FriendlyName);

    static string VolumeText(MMDevice d)
    {
        var v = d.AudioEndpointVolume;
        return string.Create(CultureInfo.InvariantCulture,
            $"\"{d.FriendlyName}\" vol={v.MasterVolumeLevelScalar:0.000} ({v.MasterVolumeLevel:0.0} dB) mute={(v.Mute ? 1 : 0)}");
    }

    static string FormatText(MMDevice d)
    {
        using var client = d.AudioClient;
        var f = client.MixFormat;
        return $"{f.SampleRate}Hz {f.Channels}ch {f.BitsPerSample}{(KindOf(f) == SampleKind.Float32 ? "f" : "i")}";
    }

    static string HwText(MMDevice d)
    {
        uint hw = (uint)d.AudioEndpointVolume.HardwareSupport;
        return $"{((hw & 1) != 0 ? "vol" : "-")},{((hw & 2) != 0 ? "mute" : "-")},{((hw & 4) != 0 ? "meter" : "-")}";
    }

    static SampleKind? KindOf(WaveFormat f)
    {
        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (f is WaveFormatExtensible x && x.SubFormat == IeeeFloat);
        bool isPcm = f.Encoding == WaveFormatEncoding.Pcm || (f is WaveFormatExtensible && !isFloat);
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
