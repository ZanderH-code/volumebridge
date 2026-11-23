namespace VolumeBridge.Core;

/// <summary>Where config.json and logs live.</summary>
public static class AppPaths
{
    /// <summary>
    /// Next to the exe for a portable copy. When the exe is installed under Program Files (not writable
    /// by the unelevated user, and must stay that way), use %LOCALAPPDATA%\VolumeBridge instead.
    /// </summary>
    public static string DataDir(string exeDir, IEnumerable<string?> programFilesDirs, string localAppData)
    {
        string exe = Normalize(exeDir);
        foreach (var pf in programFilesDirs)
        {
            if (string.IsNullOrEmpty(pf)) continue;
            string root = Normalize(pf);
            if (exe.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(localAppData, "VolumeBridge");
        }
        return exeDir;
    }

    public static string DataDir() => DataDir(
        AppContext.BaseDirectory,
        [Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
         Environment.GetEnvironmentVariable("ProgramW6432")],
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    static string Normalize(string p) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;
}
