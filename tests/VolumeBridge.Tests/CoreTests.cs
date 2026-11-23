using VolumeBridge.Core;
using Xunit;

namespace VolumeBridge.Tests;

public class DeviceMatcherTests
{
    static readonly AppConfig Cfg = new();
    static DeviceInfo D(string id, string name) => new(id, name);

    [Fact]
    public void Virtual_FirstCompleteCandidateWins_SkippingHalfInstalledOnes()
    {
        // Virtual Cables only has a playback side present; VB-CABLE has both.
        var renders = new[] { D("r1", "Speakers (Virtual Cable 01)"), D("r2", "CABLE Input (VB-Audio Virtual Cable)") };
        var captures = new[] { D("c2", "CABLE Output (VB-Audio Virtual Cable)") };

        var m = DeviceMatcher.FindVirtual(Cfg.VirtualCandidates, renders, captures);

        Assert.Equal("VB-CABLE", m!.Candidate.Name);
        Assert.Equal("r2", m.Render.Id);
        Assert.Equal("c2", m.Capture.Id);
    }

    [Fact]
    public void Output_MatchesZgmicroName()
    {
        var renders = new[] { D("v", "Virtual Cable 01"), D("j", "Speakers (Zgmicro AUDIO)") };
        var m = DeviceMatcher.FindOutput(Cfg.OutputKeywords, null, renders, "v");
        Assert.Equal("j", m.Device!.Id);
    }

    [Fact]
    public void Output_RefusesVirtualRenderEndpoint()
    {
        var renders = new[] { D("v", "JBL Virtual Cable") };
        var m = DeviceMatcher.FindOutput(Cfg.OutputKeywords, null, renders, "v");
        Assert.Null(m.Device);
        Assert.Contains("feedback", m.Error);
    }

    [Fact]
    public void Output_RefusesAmbiguousMatch_UntilDeviceIdGiven()
    {
        var renders = new[] { D("a", "JBL Flip 6"), D("b", "JBL Charge 5") };
        Assert.Null(DeviceMatcher.FindOutput(Cfg.OutputKeywords, null, renders, null).Device);
        Assert.Equal("b", DeviceMatcher.FindOutput(Cfg.OutputKeywords, "b", renders, null).Device!.Id);
    }
}

public class LevelMeterTests
{
    [Fact]
    public void Float32_FullScaleSquare_IsZeroDb_AndReadResets()
    {
        var m = new LevelMeter();
        var bytes = new float[] { 1f, -1f, 1f, -1f }.SelectMany(BitConverter.GetBytes).ToArray();
        m.Add(bytes, SampleKind.Float32);

        var (rms, peak) = m.Read();
        Assert.Equal(0, rms, 6);
        Assert.Equal(0, peak, 6);
        Assert.True(double.IsNegativeInfinity(m.Read().RmsDb));
    }

    [Fact]
    public void Pcm24_HalfScaleNegative_IsMinus6Db()
    {
        var m = new LevelMeter();
        // -4194304 = 0xC00000 little-endian: 00 00 C0
        m.Add(new byte[] { 0x00, 0x00, 0xC0 }, SampleKind.Pcm24);
        Assert.Equal(-6.02, m.Read().PeakDb, 2);
    }
}

public class AppConfigTests
{
    [Fact]
    public void Json_RoundTripsAndToleratesComments()
    {
        var json = "{ // note\n \"outputKeywords\": [\"Flip\"], \"targetBufferMs\": 25, }";
        var c = AppConfig.FromJson(json);
        Assert.Equal(["Flip"], c.OutputKeywords);
        Assert.Equal(25, c.TargetBufferMs);
        Assert.Equal(2, AppConfig.FromJson(c.ToJson()).VirtualCandidates.Count);
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
