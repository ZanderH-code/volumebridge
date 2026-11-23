using System.Text.Json;
using System.Text.Json.Serialization;

namespace VolumeBridge.Core;

public sealed class AppConfig
{
    public const string FileName = "config.json";

    /// <summary>Any keyword match (case-insensitive) selects the speaker automatically.</summary>
    public List<string> OutputKeywords { get; set; } = ["JBL", "Zgmicro"];

    /// <summary>Exact endpoint ID; overrides <see cref="OutputKeywords"/> when set.</summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>Output at 100 % slider, in dB (0 = the speaker's full level). Keeps a fixed-max speaker sane.</summary>
    public double MaxOutputDb { get; set; } = -12;

    /// <summary>Linear factor for <see cref="MaxOutputDb"/>, limited to -60..0 dB.</summary>
    [JsonIgnore]
    public float OutputCapGain => (float)Math.Pow(10, Math.Clamp(MaxOutputDb, -60, 0) / 20.0);

    /// <summary>UI language: "auto" (installer choice, else Windows), "zh" or "en".</summary>
    public string Language { get; set; } = "auto";

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads config from <paramref name="path"/>, writing defaults first if the file is missing.
    /// Settings of older versions that no longer exist are ignored.</summary>
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
