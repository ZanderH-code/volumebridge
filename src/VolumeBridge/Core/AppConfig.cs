using System.Globalization;
using System.Text;

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
    public float OutputCapGain => (float)Math.Pow(10, Math.Max(-60, Math.Min(0, MaxOutputDb)) / 20.0);

    /// <summary>UI language: "auto" (Windows display language), "zh" or "en".</summary>
    public string Language { get; set; } = "auto";

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

    public static AppConfig FromJson(string json)
    {
        var cfg = new AppConfig();
        if (MiniJson.Parse(json) is not Dictionary<string, object?> obj) return cfg;

        if (obj.TryGetValue("outputKeywords", out var kwVal) && kwVal is List<object?> kwList)
            cfg.OutputKeywords = kwList.OfType<string>().ToList();

        if (obj.TryGetValue("outputDeviceId", out var idVal))
            cfg.OutputDeviceId = idVal as string;

        if (obj.TryGetValue("maxOutputDb", out var dbVal) && dbVal is double db)
            cfg.MaxOutputDb = db;

        if (obj.TryGetValue("language", out var langVal) && langVal is string lang)
            cfg.Language = lang;

        return cfg;
    }

    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.Append("  \"outputKeywords\": [");
        for (int i = 0; i < OutputKeywords.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(MiniJson.Quote(OutputKeywords[i]));
        }
        sb.AppendLine("],");
        sb.Append("  \"outputDeviceId\": ");
        sb.Append(OutputDeviceId is null ? "null" : MiniJson.Quote(OutputDeviceId));
        sb.AppendLine(",");
        sb.Append("  \"maxOutputDb\": ");
        sb.Append(MaxOutputDb.ToString("G", CultureInfo.InvariantCulture));
        sb.AppendLine(",");
        sb.Append("  \"language\": ");
        sb.AppendLine(MiniJson.Quote(Language));
        sb.Append("}");
        return sb.ToString();
    }
}
