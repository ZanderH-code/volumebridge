using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using VolumeBridge.Core.Usb;
using Xunit;

namespace VolumeBridge.Tests;

public class SpeakerDescriptorTests
{
    [Fact]
    public void Configuration_IsWellFormed_PlaybackOnly()
    {
        var c = SpeakerDescriptors.Configuration;
        Assert.Equal(c.Length, BinaryPrimitives.ReadUInt16LittleEndian(c.AsSpan(2)));
        Assert.Equal(2, c[4]); // bNumInterfaces

        int pos = 0, acClassBytes = 0, acHeaderTotal = -1, endpoints = 0;
        int currentInterface = -1;
        while (pos < c.Length)
        {
            int len = c[pos];
            Assert.True(len >= 2 && pos + len <= c.Length, $"bad descriptor at {pos}");
            byte type = c[pos + 1];
            if (type == SpeakerDescriptors.TypeInterface) currentInterface = c[pos + 2];
            if (type == SpeakerDescriptors.TypeCsInterface && currentInterface == 0)
            {
                acClassBytes += len;
                if (c[pos + 2] == 0x01) // AC header
                {
                    acHeaderTotal = BinaryPrimitives.ReadUInt16LittleEndian(c.AsSpan(pos + 5));
                    Assert.Equal(1, c[pos + 7]); // bInCollection
                    Assert.Equal(1, c[pos + 8]); // baInterfaceNr(1)
                }
            }
            if (type == SpeakerDescriptors.TypeEndpoint)
            {
                endpoints++;
                Assert.Equal(SpeakerDescriptors.OutEndpoint, c[pos + 2]);
            }
            pos += len;
        }
        Assert.Equal(acClassBytes, acHeaderTotal);
        Assert.Equal(1, endpoints);
    }
}

public class SpeakerDeviceTests
{
    static readonly VolumeRange Range = VolumeRange.FromDb(-60, 0, 0.25);

    static (byte[] Data, int Status) Ctrl(SpeakerDevice d, byte type, byte req, ushort value, ushort index, ushort len, params byte[] data) =>
        d.HandleControl(new SetupPacket(type, req, value, index, len), data);

    const ushort FeatureUnitIndex = SpeakerDescriptors.FeatureUnitId << 8;

    [Fact]
    public void VolumeRange_SatisfiesWindowsConstraints()
    {
        Assert.Equal(new VolumeRange(-60 * 256, 0, 64), Range);
        var odd = VolumeRange.FromDb(-50.3, -0.1, 0.3);
        Assert.Equal(0, (odd.Max - odd.Min) % odd.Res);
        Assert.True(odd.Min < odd.Max);
    }

    [Fact]
    public void Volume_SetCur_ClampsAndReportsChange_SilenceMeansZeroGain()
    {
        var d = new SpeakerDevice(Range);
        var seen = new List<(short, bool)>();
        d.VolumeChanged += (v, m) => seen.Add((v, m));

        Assert.Equal(0, Ctrl(d, 0x21, 0x01, 0x0200, FeatureUnitIndex, 2, 0x00, 0xF6).Status); // -10 dB
        Assert.Equal(0, Ctrl(d, 0x21, 0x01, 0x0200, FeatureUnitIndex, 2, 0x00, 0x80).Status); // 0x8000 = -inf
        Assert.Equal(0, Ctrl(d, 0x21, 0x01, 0x0200, FeatureUnitIndex, 2, 0x00, 0x90).Status); // -112 dB -> clamp -60

        Assert.Equal([((short)(-10 * 256), false), (SpeakerDevice.VolumeSilence, false), ((short)(-60 * 256), false)], seen);
        Assert.Equal(0.0, SpeakerDevice.Gain(SpeakerDevice.VolumeSilence, false));
        Assert.Equal(0.1, SpeakerDevice.Gain(-20 * 256, false), 6);
        Assert.Equal(0.0, SpeakerDevice.Gain(0, true));
    }

    [Fact]
    public void Volume_GetRange_AndMute_RoundTrip()
    {
        var d = new SpeakerDevice(Range);
        Assert.Equal(new byte[] { 0x00, 0xC4 }, Ctrl(d, 0xA1, 0x82, 0x0200, FeatureUnitIndex, 2).Data); // -60 dB
        Assert.Equal(new byte[] { 0x40, 0x00 }, Ctrl(d, 0xA1, 0x84, 0x0200, FeatureUnitIndex, 2).Data); // 0.25 dB
        short initial = BinaryPrimitives.ReadInt16LittleEndian(Ctrl(d, 0xA1, 0x81, 0x0200, FeatureUnitIndex, 2).Data);
        Assert.InRange(initial, Range.Min, Range.Max);

        Ctrl(d, 0x21, 0x01, 0x0100, FeatureUnitIndex, 1, 1);
        Assert.Equal(new byte[] { 1 }, Ctrl(d, 0xA1, 0x81, 0x0100, FeatureUnitIndex, 1).Data);
        Assert.True(d.VolumeState.Mute);
    }

    [Fact]
    public void PerChannelAndUnknownEntity_Stall()
    {
        var d = new SpeakerDevice(Range);
        Assert.Equal(SpeakerDevice.Stall, Ctrl(d, 0xA1, 0x81, 0x0201, FeatureUnitIndex, 2).Status); // channel 1
        Assert.Equal(SpeakerDevice.Stall, Ctrl(d, 0xA1, 0x81, 0x0200, 5 << 8, 2).Status);          // entity 5
    }

    [Fact]
    public void Streaming_RequiresConfigurationThenAlt1()
    {
        var d = new SpeakerDevice(Range);
        Assert.Equal(SpeakerDevice.Stall, Ctrl(d, 0x01, 0x0B, 1, 1, 0).Status);
        Ctrl(d, 0x00, 0x09, 1, 0, 0);
        Ctrl(d, 0x01, 0x0B, 1, 1, 0);
        Assert.True(d.PlaybackActive);
        Assert.Equal(SpeakerDevice.Stall, Ctrl(d, 0x01, 0x0B, 1, 2, 0).Status); // interface 2 does not exist
        Ctrl(d, 0x01, 0x0B, 0, 1, 0);
        Assert.False(d.PlaybackActive);
    }
}

public class IsoClockTests
{
    [Fact]
    public void Reservations_AreContiguous_AndRestartFromNowWhenIdle()
    {
        var c = new IsoClock();
        long ms = Stopwatch.Frequency / 1000;
        long a = c.Reserve(10, 1000 * ms);
        long b = c.Reserve(10, 1001 * ms);
        Assert.Equal(1010 * ms, a);
        Assert.Equal(1020 * ms, b);
        Assert.Equal(2010 * ms, c.Reserve(10, 2000 * ms));
    }
}

public class UsbipCliParseTests
{
    const string PortOutput = """
        Imported USB devices
        ====================
        Port 01: device in use at Full Speed(12Mbps)
                 unknown vendor : unknown product (ffff:5642)
                   -> usbip://127.0.0.1:3240/1-1
                   -> remote bus/dev: 001/001
        Port 02: device in use at Full Speed(12Mbps)
                 unknown vendor : unknown product (ffff:ca01)
                   -> usbip://127.0.0.1:3241/1-1
        """;

    [Fact]
    public void OwnPorts_MatchHostPortAndBus()
    {
        Assert.Equal([1], UsbipCli.ParseOwnPorts(PortOutput, 3240));
        Assert.Equal([2], UsbipCli.ParseOwnPorts(PortOutput, 3241));
    }

    [Fact]
    public void AttachedPort_FromMessageOrTerseNumber()
    {
        Assert.Equal(3, UsbipCli.ParseAttachedPort("successfully attached to port 3\r\n"));
        Assert.Equal(4, UsbipCli.ParseAttachedPort("4\n"));
        Assert.Null(UsbipCli.ParseAttachedPort("error: cannot connect"));
    }
}

/// <summary>Drives the server over real TCP like usbip-win2 would.</summary>
public sealed class UsbipServerTests : IDisposable
{
    readonly SpeakerDevice _device = new(VolumeRange.FromDb(-60, 0, 0.25));
    readonly List<byte> _pcm = [];
    readonly UsbipServer _server;
    readonly TcpClient _client;
    readonly NetworkStream _s;
    uint _seq;

    public UsbipServerTests()
    {
        _server = new UsbipServer(_device, p => { lock (_pcm) _pcm.AddRange(p.ToArray()); }, new SleepWaiter(), _ => { }, 0);
        _server.Start();
        _client = new TcpClient("127.0.0.1", _server.Port) { ReceiveTimeout = 5000 };
        _s = _client.GetStream();

        var req = new byte[8 + 32];
        BinaryPrimitives.WriteUInt16BigEndian(req, 0x0111);
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(2), 0x8003);
        Encoding.ASCII.GetBytes("1-1").CopyTo(req, 8);
        _s.Write(req);
        var rep = Read(8 + 312);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(rep.AsSpan(4)));
        Assert.Equal(0x5642, BinaryPrimitives.ReadUInt16BigEndian(rep.AsSpan(8 + 302)));
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    byte[] Read(int n)
    {
        var b = new byte[n];
        _s.ReadExactly(b);
        return b;
    }

    uint Submit(uint dir, uint ep, byte[] setup, byte[] outData, int length, int isoPackets = -1)
    {
        uint seq = ++_seq;
        var h = new byte[48];
        BinaryPrimitives.WriteUInt32BigEndian(h, 1);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), seq);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), 0x00010001);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12), dir);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(16), ep);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(24), length);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(32), isoPackets < 0 ? 0xFFFFFFFF : (uint)isoPackets);
        setup.CopyTo(h, 40);
        var frame = new List<byte>(h);
        frame.AddRange(outData);
        for (int i = 0; i < Math.Max(isoPackets, 0); i++)
        {
            var d = new byte[16];
            BinaryPrimitives.WriteInt32BigEndian(d, i * 192);
            BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(4), 192);
            frame.AddRange(d);
        }
        _s.Write(frame.ToArray());
        return seq;
    }

    (uint Seq, int Status, byte[] Data) ControlIn(byte type, byte req, ushort value, ushort index, ushort len)
    {
        uint seq = Submit(1, 0, Setup(type, req, value, index, len), [], len);
        var h = Read(48);
        int actual = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(24));
        return (BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(4)), BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(20)), Read(actual));
    }

    int ControlOut(byte type, byte req, ushort value, ushort index, params byte[] data)
    {
        Submit(0, 0, Setup(type, req, value, index, (ushort)data.Length), data, data.Length);
        return BinaryPrimitives.ReadInt32BigEndian(Read(48).AsSpan(20));
    }

    static byte[] Setup(byte type, byte req, ushort value, ushort index, ushort len)
    {
        var s = new byte[8];
        s[0] = type; s[1] = req;
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(2), value);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(4), index);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(6), len);
        return s;
    }

    [Fact]
    public void Enumeration_VolumeAndPacedIsoStream()
    {
        var (_, st, dev) = ControlIn(0x80, 0x06, 0x0100, 0, 64);
        Assert.Equal(0, st);
        Assert.Equal(SpeakerDescriptors.Device, dev);
        Assert.Equal(0, ControlOut(0x00, 0x09, 1, 0));
        Assert.Equal(0, ControlOut(0x01, 0x0B, 1, 1));
        Assert.Equal(0, ControlOut(0x21, 0x01, 0x0200, SpeakerDescriptors.FeatureUnitId << 8, 0x00, 0xF6));
        Assert.Equal((short)(-10 * 256), _device.VolumeState.Volume);

        var pcm = Enumerable.Range(0, 1920).Select(i => (byte)i).ToArray();
        var sw = Stopwatch.StartNew();
        uint a = Submit(0, 1, new byte[8], pcm, pcm.Length, 10);
        uint b = Submit(0, 1, new byte[8], pcm, pcm.Length, 10);

        var ra = Read(48 + 160);
        double tA = sw.Elapsed.TotalMilliseconds;
        var rb = Read(48 + 160);
        double tB = sw.Elapsed.TotalMilliseconds;

        Assert.Equal(a, BinaryPrimitives.ReadUInt32BigEndian(ra.AsSpan(4)));
        Assert.Equal(b, BinaryPrimitives.ReadUInt32BigEndian(rb.AsSpan(4)));
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32BigEndian(ra.AsSpan(32)));
        Assert.Equal(192, BinaryPrimitives.ReadInt32BigEndian(ra.AsSpan(48 + 8))); // packet 0 actual length
        Assert.InRange(tA, 8, 200);
        Assert.InRange(tB - tA, 5, 200);   // second URB completes ~10 ms after the first
        lock (_pcm) Assert.Equal(pcm.Concat(pcm), _pcm);
    }

    [Fact]
    public void SecondImport_ReplacesFirstSession()
    {
        Assert.Equal(0, ControlOut(0x00, 0x09, 1, 0));
        using var second = new TcpClient("127.0.0.1", _server.Port) { ReceiveTimeout = 5000 };
        var s2 = second.GetStream();
        var req = new byte[8 + 32];
        BinaryPrimitives.WriteUInt16BigEndian(req, 0x0111);
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(2), 0x8003);
        Encoding.ASCII.GetBytes("1-1").CopyTo(req, 8);
        s2.Write(req);
        s2.ReadExactly(new byte[8 + 312]);

        // Old connection is closed by the server; device state was reset for the new session.
        Assert.ThrowsAny<Exception>(() => Read(1));
        // The import reply precedes the hand-off; give it a moment.
        Assert.True(SpinWait.SpinUntil(() => _server.SessionActive, 2000));
        Assert.False(_device.PlaybackActive);
    }

    [Fact]
    public void Unlink_OfQueuedUrb_RepliesOnlyWithRetUnlink()
    {
        ControlOut(0x00, 0x09, 1, 0);
        ControlOut(0x01, 0x0B, 1, 1);
        var pcm = new byte[1920];
        uint first = Submit(0, 1, new byte[8], pcm, pcm.Length, 10);
        uint victim = Submit(0, 1, new byte[8], pcm, pcm.Length, 10);

        var u = new byte[48];
        BinaryPrimitives.WriteUInt32BigEndian(u, 2);
        BinaryPrimitives.WriteUInt32BigEndian(u.AsSpan(4), 99);
        BinaryPrimitives.WriteUInt32BigEndian(u.AsSpan(20), victim);
        _s.Write(u);

        var replies = new List<(uint Cmd, uint Seq, int Status)>();
        for (int i = 0; i < 2; i++)
        {
            var h = Read(48);
            uint cmd = BinaryPrimitives.ReadUInt32BigEndian(h);
            if (cmd == 3) Read(16 * 10);
            replies.Add((cmd, BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(4)), BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(20))));
        }
        Assert.Contains((4u, 99u, -104), replies);
        Assert.Contains((3u, first, 0), replies);

        // Nothing else (no RET_SUBMIT for the victim) arrives.
        _client.ReceiveTimeout = 100;
        Assert.ThrowsAny<IOException>(() => Read(48));
    }
}

public class ReviewRegressionTests
{
    [Fact]
    public void PortReset_IsAccepted_AndKeepsStreamingState()
    {
        var d = new SpeakerDevice(VolumeRange.FromDb(-60, 0, 0.25));
        d.HandleControl(new SetupPacket(0x00, 0x09, 1, 0, 0), []);
        d.HandleControl(new SetupPacket(0x01, 0x0B, 1, 1, 0), []);
        Assert.Equal(0, d.HandleControl(new SetupPacket(0x23, 0x03, 4, 1, 0), []).Status);
        Assert.True(d.PlaybackActive);
        Assert.Equal(SpeakerDevice.Stall, d.HandleControl(new SetupPacket(0x41, 0x01, 0, 0, 0), []).Status); // vendor
    }

    [Theory]
    [InlineData(-127, -127, 10)]
    [InlineData(-127, -127, 1)]
    [InlineData(-200, 5, 0.25)]
    public void VolumeRange_NeverWrapsOrUsesSilenceValue(double min, double max, double step)
    {
        var r = VolumeRange.FromDb(min, max, step);
        Assert.True(r.Min > short.MinValue && r.Min < r.Max && r.Max <= 0);
        Assert.Equal(0, (r.Max - r.Min) % r.Res);
    }
}
