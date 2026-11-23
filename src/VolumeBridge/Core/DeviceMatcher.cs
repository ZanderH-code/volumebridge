namespace VolumeBridge.Core;

public sealed record DeviceInfo(string Id, string Name);

public sealed record OutputMatch(DeviceInfo? Device, string? Error);

/// <summary>Pure speaker-selection rules, kept free of COM so they can be unit tested anywhere.</summary>
public static class DeviceMatcher
{
    static bool Has(DeviceInfo d, string keyword) =>
        keyword.Length > 0 && d.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>The speaker: the exact <paramref name="deviceId"/> if set, else the single keyword match.</summary>
    public static OutputMatch FindOutput(IReadOnlyList<string> keywords, string? deviceId, IReadOnlyList<DeviceInfo> renders)
    {
        var hits = !string.IsNullOrWhiteSpace(deviceId)
            ? renders.Where(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)).ToList()
            : renders.Where(d => keywords.Any(k => Has(d, k))).ToList();

        return hits.Count switch
        {
            0 => new OutputMatch(null, Loc.T("没有找到匹配的播放设备", "no matching playback device")),
            1 => new OutputMatch(hits[0], null),
            _ => new OutputMatch(null, Loc.T(
                $"有 {hits.Count} 个设备匹配，请在“输出设备”中选择一个",
                $"{hits.Count} devices match; pick one under \"Output device\"")),
        };
    }

    /// <summary>All keyword matches (setup may enable several speakers).</summary>
    public static List<DeviceInfo> AllMatches(IReadOnlyList<string> keywords, string? deviceId, IReadOnlyList<DeviceInfo> renders) =>
        !string.IsNullOrWhiteSpace(deviceId)
            ? renders.Where(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)).ToList()
            : renders.Where(d => keywords.Any(k => Has(d, k))).ToList();
}
