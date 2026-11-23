namespace VolumeBridge.Core;

/// <summary>Where config.json and logs live.</summary>
public static class AppPaths
{
    /// <summary>Always %LOCALAPPDATA%\VolumeBridge so a portable exe never scatters files in Downloads.</summary>
    public static string DataDir(string localAppData) => Path.Combine(localAppData, "VolumeBridge");

    public static string DataDir() => DataDir(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
}
