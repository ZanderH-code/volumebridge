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
