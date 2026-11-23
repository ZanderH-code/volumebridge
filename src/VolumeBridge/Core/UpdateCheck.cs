using System.Security.Cryptography;
using System.Text.Json;

namespace VolumeBridge.Core;

public sealed record ReleaseInfo(Version Version, string Tag, string SetupUrl, string ChecksumUrl, string PageUrl);

/// <summary>Latest-release lookup on GitHub Releases (public repo, no token).</summary>
public static class UpdateCheck
{
    public const string Repo = "ZanderH-code/volumebridge";
    public const string SetupAsset = "VolumeBridgeSetup.exe";
    public const string ChecksumAsset = "VolumeBridgeSetup.exe.sha256";
    static readonly string LatestUrl = $"https://api.github.com/repos/{Repo}/releases/latest";

    /// <summary>Parses the GitHub "latest release" JSON; null if it lacks a version tag or the two assets.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version)) return null;

        string? setup = null, sum = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            string url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (!url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.Ordinal)) continue;
            if (name == SetupAsset) setup = url;
            else if (name == ChecksumAsset) sum = url;
        }
        string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        return setup is null || sum is null ? null : new ReleaseInfo(version, tag, setup, sum, page);
    }

    /// <summary>"v1.2.3" / "1.2.3" → 1.2.3 (missing parts are 0).</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        string t = tag.Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(['-', '+']);
        if (dash >= 0) t = t[..dash];
        if (Version.TryParse(t.Contains('.') ? t : t + ".0", out var v))
        {
            version = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    public static bool IsNewer(Version candidate, Version current) =>
        candidate > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

    /// <summary>First token of a "sha256sum"-style line, lower-case hex.</summary>
    public static string? ParseChecksum(string text)
    {
        string first = text.Trim().Split([' ', '\t', '\r', '\n'], 2)[0].ToLowerInvariant();
        return first.Length == 64 && first.All(Uri.IsHexDigit) ? first : null;
    }

    public static async Task<ReleaseInfo?> GetLatestAsync(HttpClient http, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release yet
        resp.EnsureSuccessStatusCode();
        return Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Downloads the installer to <paramref name="dir"/> and verifies it against the published SHA-256.</summary>
    public static async Task<string> DownloadVerifiedAsync(HttpClient http, ReleaseInfo r, string dir, IProgress<double>? progress, CancellationToken ct)
    {
        string expected = ParseChecksum(await http.GetStringAsync(r.ChecksumUrl, ct))
            ?? throw new InvalidDataException("发布页上的校验文件格式不正确");

        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"VolumeBridgeSetup-{r.Version}.exe");
        using (var resp = await http.GetAsync(r.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
        }

        string actual;
        await using (var f = File.OpenRead(path)) actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
        if (actual != expected)
        {
            File.Delete(path);
            throw new InvalidDataException("下载的安装包校验失败，已删除");
        }
        return path;
    }
}
