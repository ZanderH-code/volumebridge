using System.Text.Json;
using System.Text.Json.Serialization;

namespace VolumeBridge.Core;

/// <summary>A virtual cable product: keywords that identify its render (playback) and capture (recording) endpoints.</summary>
public sealed record VirtualCandidate(string Name, string RenderKeyword, string CaptureKeyword);

public sealed class AppConfig
{
    public const string FileName = "config.json";

    /// <summary>Tried in order; the first candidate with both endpoints present wins.</summary>
    public List<VirtualCandidate> VirtualCandidates { get; set; } =
    [
        // Specific cable number: a bare "Virtual Cable" also matches "CABLE Output (VB-Audio Virtual Cable)".
        new("Virtual Cables", "Virtual Cable 01", "Virtual Cable 01"),
        new("VB-CABLE", "CABLE Input", "CABLE Output"),
    ];

    /// <summary>Any keyword match (case-insensitive) selects the output render endpoint.</summary>
    public List<string> OutputKeywords { get; set; } = ["JBL", "Zgmicro"];

    /// <summary>Exact endpoint ID; overrides <see cref="OutputKeywords"/> when set.</summary>
    public string? OutputDeviceId { get; set; }

    public int TargetBufferMs { get; set; } = 30;

    /// <summary>Volume range and step advertised by "VolumeBridge Speaker" to Windows.</summary>
    public double MinDb { get; set; } = -60;
    public double MaxDb { get; set; } = 0;
    public double VolumeStepDb { get; set; } = 0.25;

    /// <summary>Local TCP port of the built-in USB/IP server.</summary>
    public int UsbipPort { get; set; } = 3240;

    /// <summary>Full path to usbip.exe; null = default install folders / PATH.</summary>
    public string? UsbipPath { get; set; }
    public int SmoothingMs { get; set; } = 20;

    /// <summary>auto | apply | passthrough. See docs/PLAN.md, "双重衰减".</summary>
    public string GainMode { get; set; } = "auto";

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Loads config from <paramref name="path"/>, writing defaults first if the file is missing.</summary>
    public static AppConfig LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new AppConfig();
            File.WriteAllText(path, defaults.ToJson());
            return defaults;
        }
        return FromJson(File.ReadAllText(path));
    }

    public static AppConfig FromJson(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, Json) ?? new AppConfig();

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}
