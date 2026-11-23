using VolumeBridge.Core;
using Xunit;

namespace VolumeBridge.Tests;

public class DeviceMatcherTests
{
    static readonly AppConfig Cfg = new();
    static DeviceInfo D(string id, string name) => new(id, name);

    [Fact]
    public void FindsTheFlip6ByItsUsbChipName()
    {
        var renders = new[] { D("hdmi", "Display Audio"), D("jbl", "Speakers (Zgmicro AUDIO)") };
        Assert.Equal("jbl", DeviceMatcher.FindOutput(Cfg.OutputKeywords, null, renders).Device!.Id);
    }

    [Fact]
    public void AmbiguousMatch_NeedsAnExplicitDevice()
    {
        var renders = new[] { D("a", "JBL Flip 6"), D("b", "JBL Charge 5") };
        Assert.Null(DeviceMatcher.FindOutput(Cfg.OutputKeywords, null, renders).Device);
        Assert.Equal("b", DeviceMatcher.FindOutput(Cfg.OutputKeywords, "b", renders).Device!.Id);
        Assert.Equal(2, DeviceMatcher.AllMatches(Cfg.OutputKeywords, null, renders).Count); // setup enables both
    }
}

public class AppConfigTests
{
    [Fact]
    public void ReadsOlderConfigs_IgnoringRemovedSettings()
    {
        var json = "{ // 0.3.x config\n \"mode\": \"virtual\", \"usbipPort\": 3240, \"outputKeywords\": [\"Flip\"], \"maxOutputDb\": -20, }";
        var c = AppConfig.FromJson(json);
        Assert.Equal(["Flip"], c.OutputKeywords);
        Assert.Equal(-20, c.MaxOutputDb);
        Assert.Equal(0.1f, c.OutputCapGain, 3);
        Assert.DoesNotContain("outputCapGain", c.ToJson(), StringComparison.OrdinalIgnoreCase);
    }
}

public class AppPathsTests
{
    [Fact]
    public void ProgramFilesInstall_UsesLocalAppData_PortableUsesExeDir()
    {
        string pf = Path.Combine(Path.GetTempPath(), "pf");
        string lad = Path.Combine(Path.GetTempPath(), "lad");
        Assert.Equal(Path.Combine(lad, "VolumeBridge"), AppPaths.DataDir(Path.Combine(pf, "VolumeBridge") + Path.DirectorySeparatorChar, [null, pf], lad));

        string portable = Path.Combine(Path.GetTempPath(), "pfx", "VolumeBridge");
        Assert.Equal(portable, AppPaths.DataDir(portable, [pf], lad)); // "pfx" is not under "pf"
    }
}

public class UpdateCheckTests
{
    static string Release(string tag, bool prerelease = false, string owner = "ZanderH-code/volumebridge") => $$"""
        {"tag_name":"{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}},"html_url":"https://github.com/x",
         "assets":[
          {"name":"VolumeBridgeSetup.exe","browser_download_url":"https://github.com/{{owner}}/releases/download/{{tag}}/VolumeBridgeSetup.exe"},
          {"name":"VolumeBridgeSetup.exe.sha256","browser_download_url":"https://github.com/{{owner}}/releases/download/{{tag}}/VolumeBridgeSetup.exe.sha256"}]}
        """;

    [Fact]
    public void Parse_ReadsVersionAndAssets()
    {
        var r = UpdateCheck.Parse(Release("v0.3.1"))!;
        Assert.Equal(new Version(0, 3, 1), r.Version);
        Assert.EndsWith("/v0.3.1/VolumeBridgeSetup.exe", r.SetupUrl);
    }

    [Fact]
    public void Parse_RejectsPrereleaseAndForeignDownloadHosts()
    {
        Assert.Null(UpdateCheck.Parse(Release("v0.3.1", prerelease: true)));
        Assert.Null(UpdateCheck.Parse(Release("v0.3.1", owner: "someone/else")));
    }

    [Theory]
    [InlineData("v0.2.1", "0.2.0", true)]
    [InlineData("v0.2.0", "0.2.0", false)]
    [InlineData("v0.10.0", "0.9.9", true)]
    [InlineData("v1", "0.9.9", true)]
    [InlineData("v0.1.9-beta", "0.2.0", false)]
    public void IsNewer_ComparesNumerically(string tag, string current, bool newer)
    {
        Assert.True(UpdateCheck.TryParseVersion(tag, out var v));
        Assert.Equal(newer, UpdateCheck.IsNewer(v, Version.Parse(current)));
    }

    [Fact]
    public void Checksum_FirstTokenOfSha256sumLine()
    {
        string h = new('a', 64);
        Assert.Equal(h, UpdateCheck.ParseChecksum($"{h.ToUpperInvariant()}  VolumeBridgeSetup.exe\n"));
        Assert.Null(UpdateCheck.ParseChecksum("not a hash"));
    }
}

public class FileLogTests
{
    [Fact]
    public void RollsOverAtLimit()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vb-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "volumebridge.log");
        using (var log = new FileLog(path, maxBytes: 1000))
            for (int i = 0; i < 100; i++) log.Write(new string('x', 50));
        Assert.True(File.Exists(Path.Combine(dir, "volumebridge.1.log")));
        Assert.True(new FileInfo(path).Length <= 1100);
        Directory.Delete(dir, true);
    }
}

public class LocTests
{
    static readonly System.Globalization.CultureInfo Zh = new("zh-CN"), En = new("en-US"), De = new("de-DE");

    [Fact]
    public void ConfigWins_ThenInstallerChoice_ThenWindowsLanguage()
    {
        Assert.Equal(UiLanguage.English, Loc.Resolve("en", "zh", Zh));
        Assert.Equal(UiLanguage.Chinese, Loc.Resolve("auto", "zh", En));
        Assert.Equal(UiLanguage.Chinese, Loc.Resolve(null, null, Zh));
        Assert.Equal(UiLanguage.English, Loc.Resolve("auto", "", De));
    }
}
