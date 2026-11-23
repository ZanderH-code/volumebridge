using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace VolumeBridge.Core;

public sealed record ReleaseInfo(Version Version, string Tag, string ExeUrl, string ChecksumUrl, string PageUrl);

/// <summary>Latest-release lookup on GitHub Releases (public repo, no token).</summary>
public static class UpdateCheck
{
    public const string Repo = "ZanderH-code/volumebridge";
    public const string ExeAsset = "VolumeBridge.exe";
    public const string ChecksumAsset = "VolumeBridge.exe.sha256";
    static readonly string LatestUrl = $"https://api.github.com/repos/{Repo}/releases/latest";

#if NETFRAMEWORK
    static UpdateCheck()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
    }
#endif

    /// <summary>Parses the GitHub "latest release" JSON; null if it lacks a version tag or the two assets.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        if (MiniJson.Parse(json) is not Dictionary<string, object?> root) return null;
        if (root.TryGetValue("draft", out var draft) && draft is true) return null;
        if (root.TryGetValue("prerelease", out var pre) && pre is true) return null;
        string tag = root.TryGetValue("tag_name", out var tagVal) ? tagVal as string ?? "" : "";
        if (!TryParseVersion(tag, out var version)) return null;

        string? exe = null, sum = null;
        if (root.TryGetValue("assets", out var assetsVal) && assetsVal is List<object?> assets)
        {
            foreach (var item in assets)
            {
                if (item is not Dictionary<string, object?> a) continue;
                string name = a.TryGetValue("name", out var nv) ? nv as string ?? "" : "";
                string url = a.TryGetValue("browser_download_url", out var uv) ? uv as string ?? "" : "";
                if (!url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.Ordinal)) continue;
                if (name == ExeAsset) exe = url;
                else if (name == ChecksumAsset) sum = url;
            }
        }
        string page = root.TryGetValue("html_url", out var h) ? h as string ?? "" : "";
        return exe is null || sum is null ? null : new ReleaseInfo(version, tag, exe, sum, page);
    }

    /// <summary>"v1.2.3" / "1.2.3" → 1.2.3 (missing parts are 0).</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        string t = tag.Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(['-', '+']);
        if (dash >= 0) t = t.Substring(0, dash);
        if (Version.TryParse(t.IndexOf('.') >= 0 ? t : t + ".0", out var v))
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
        if (resp.StatusCode == HttpStatusCode.NotFound) return null; // no release yet
        resp.EnsureSuccessStatusCode();
        return Parse(await resp.Content.ReadAsStringAsync());
    }

    /// <summary>Downloads the exe to <paramref name="dir"/> and verifies it against the published SHA-256.</summary>
    public static async Task<string> DownloadVerifiedAsync(HttpClient http, ReleaseInfo r, string dir, IProgress<double>? progress, CancellationToken ct)
    {
        string checksumText;
        using (var cresp = await http.GetAsync(r.ChecksumUrl, ct))
        {
            cresp.EnsureSuccessStatusCode();
            checksumText = await cresp.Content.ReadAsStringAsync();
        }
        string expected = ParseChecksum(checksumText)
            ?? throw new InvalidDataException(Loc.T("发布页上的校验文件格式不正确", "The checksum file on the release page is malformed"));

        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"VolumeBridge-{r.Version}.exe");
        using (var resp = await http.GetAsync(r.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            using var src = await resp.Content.ReadAsStreamAsync();
            using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
            {
                await dst.WriteAsync(buf, 0, n, ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
        }

        string actual = await Task.Run(() => Sha256FileHex(path), ct);
        if (actual != expected)
        {
            File.Delete(path);
            throw new InvalidDataException(Loc.T("下载的文件校验失败，已删除", "The downloaded file failed verification and was deleted"));
        }
        return path;
    }

    public static string Sha256Hex(byte[] data)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
    }

    public static string Sha256FileHex(string path)
    {
        using var sha = SHA256.Create();
        using var f = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
    }
}
