using System.Globalization;

namespace VolumeBridge.Core;

public enum UiLanguage { Chinese, English }

/// <summary>Two-language UI text: <c>Loc.T("中文", "English")</c>.</summary>
public static class Loc
{
    public static UiLanguage Current { get; set; } = UiLanguage.English;

    public static string T(string zh, string en) => Current == UiLanguage.Chinese ? zh : en;

    /// <summary>config.json "language" ("zh"/"en") wins; otherwise the Windows display language.</summary>
    public static UiLanguage Resolve(string? configured, CultureInfo uiCulture) =>
        Parse(configured)
        ?? (uiCulture.TwoLetterISOLanguageName == "zh" ? UiLanguage.Chinese : UiLanguage.English);

    public static UiLanguage? Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "zh" or "zh-cn" or "chinese" or "chinesesimplified" => UiLanguage.Chinese,
        "en" or "english" => UiLanguage.English,
        _ => null,
    };

    public static string Code(UiLanguage l) => l == UiLanguage.Chinese ? "zh" : "en";
}
