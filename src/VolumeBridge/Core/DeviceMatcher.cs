namespace VolumeBridge.Core;

public sealed record DeviceInfo(string Id, string Name);

public sealed record VirtualMatch(VirtualCandidate Candidate, DeviceInfo Render, DeviceInfo Capture);

public sealed record OutputMatch(DeviceInfo? Device, string? Error);

/// <summary>Pure device-selection rules, kept free of COM so they can be unit tested anywhere.</summary>
public static class DeviceMatcher
{
    static bool Has(DeviceInfo d, string keyword) =>
        keyword.Length > 0 && d.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>First candidate (in config order) whose render and capture endpoints both exist.</summary>
    public static VirtualMatch? FindVirtual(
        IReadOnlyList<VirtualCandidate> candidates,
        IReadOnlyList<DeviceInfo> renders,
        IReadOnlyList<DeviceInfo> captures)
    {
        foreach (var c in candidates)
        {
            var r = renders.FirstOrDefault(d => Has(d, c.RenderKeyword));
            var p = captures.FirstOrDefault(d => Has(d, c.CaptureKeyword));
            if (r is not null && p is not null) return new VirtualMatch(c, r, p);
        }
        return null;
    }

    /// <summary>
    /// Picks the output render endpoint. Refuses the virtual render endpoint itself (feedback loop)
    /// and refuses ambiguous keyword matches.
    /// </summary>
    public static OutputMatch FindOutput(
        IReadOnlyList<string> keywords,
        string? deviceId,
        IReadOnlyList<DeviceInfo> renders,
        string? virtualRenderId)
    {
        List<DeviceInfo> hits;
        if (!string.IsNullOrWhiteSpace(deviceId))
            hits = renders.Where(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)).ToList();
        else
            hits = renders.Where(d => keywords.Any(k => Has(d, k))).ToList();

        if (virtualRenderId is not null &&
            hits.RemoveAll(d => string.Equals(d.Id, virtualRenderId, StringComparison.OrdinalIgnoreCase)) > 0 &&
            hits.Count == 0)
            return new OutputMatch(null, "output matches the virtual cable's own playback endpoint (feedback loop)");

        return hits.Count switch
        {
            0 => new OutputMatch(null, "no active playback device matches outputKeywords/outputDeviceId"),
            1 => new OutputMatch(hits[0], null),
            _ => new OutputMatch(null,
                $"{hits.Count} playback devices match; set outputDeviceId to one of: " +
                string.Join(", ", hits.Select(h => $"\"{h.Name}\" = {h.Id}"))),
        };
    }
}
