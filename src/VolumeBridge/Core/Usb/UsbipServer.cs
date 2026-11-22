using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VolumeBridge.Core.Usb;

/// <summary>Receives raw 48 kHz / 16-bit / stereo PCM from the emulated speaker.</summary>
public delegate void PcmSink(ReadOnlySpan<byte> pcm);

public sealed class UsbipStats
{
    long _isoUrbs, _frames, _unlinks, _controls, _maxLateTicks, _sessions;

    public long IsoUrbs => Interlocked.Read(ref _isoUrbs);
    public long Frames => Interlocked.Read(ref _frames);
    public long Unlinks => Interlocked.Read(ref _unlinks);
    public long Controls => Interlocked.Read(ref _controls);
    public long Sessions => Interlocked.Read(ref _sessions);

    internal void IsoUrb(int frames) { Interlocked.Increment(ref _isoUrbs); Interlocked.Add(ref _frames, frames); }
    internal void Unlink() => Interlocked.Increment(ref _unlinks);
    internal void Control() => Interlocked.Increment(ref _controls);
    internal void Session() => Interlocked.Increment(ref _sessions);

    internal void Late(long ticks)
    {
        long cur;
        while (ticks > (cur = Interlocked.Read(ref _maxLateTicks)) &&
               Interlocked.CompareExchange(ref _maxLateTicks, ticks, cur) != cur) { }
    }

    /// <summary>Largest URB completion lateness since the last call, in ms.</summary>
    public double TakeMaxLateMs() => Interlocked.Exchange(ref _maxLateTicks, 0) * 1000.0 / Stopwatch.Frequency;
}

/// <summary>
/// Minimal USB/IP (v1.1.1) server exporting one <see cref="SpeakerDevice"/> as bus ID "1-1".
/// One active import session at a time; a new import replaces the old one.
/// Protocol handling follows Virtual Cables (BSD-2, src/internal/usbip/server.go), except that an
/// unlinked URB gets only RET_UNLINK (-ECONNRESET), as the Linux USB/IP stub does.
/// </summary>
public sealed class UsbipServer : IDisposable
{
    public const string BusId = "1-1";
    const ushort Version = 0x0111;
    const ushort OpReqDevlist = 0x8005, OpRepDevlist = 0x0005, OpReqImport = 0x8003, OpRepImport = 0x0003;
    const uint CmdSubmit = 1, CmdUnlink = 2, RetSubmit = 3, RetUnlink = 4;
    const uint DirOut = 0;
    const uint NoIso = 0xFFFFFFFF;
    const int EConnReset = -104;
    const int MaxOutBytes = 1 << 20;
    const int MaxIsoPackets = 1024;

    readonly SpeakerDevice _device;
    readonly PcmSink _sink;
    readonly IPreciseWaiter _waiter;
    readonly Action<string> _log;
    readonly TcpListener _listener;
    readonly object _gate = new();
    Session? _session;
    volatile bool _disposed;

    public UsbipServer(SpeakerDevice device, PcmSink sink, IPreciseWaiter waiter, Action<string> log, int port, IPAddress? address = null)
    {
        _device = device;
        _sink = sink;
        _waiter = waiter;
        _log = log;
        _listener = new TcpListener(address ?? IPAddress.Loopback, port);
    }

    public UsbipStats Stats { get; } = new();
    public IsoClock Clock { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public bool SessionActive { get { lock (_gate) return _session is not null; } }

    public void Start()
    {
        _listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "usbip-accept" }.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        _listener.Stop();
        Session? s;
        lock (_gate) { s = _session; _session = null; }
        s?.Close();
        s?.Done.Wait(2000);
    }

    void AcceptLoop()
    {
        while (!_disposed)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch when (_disposed) { return; }
            catch (Exception e) { _log($"usbip accept failed: {e.Message}"); continue; }
            new Thread(() => HandleConnection(client)) { IsBackground = true, Name = "usbip-conn" }.Start();
        }
    }

    void HandleConnection(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 30_000;
            var stream = client.GetStream();
            Span<byte> op = stackalloc byte[8];
            stream.ReadExactly(op);
            ushort version = BinaryPrimitives.ReadUInt16BigEndian(op);
            ushort code = BinaryPrimitives.ReadUInt16BigEndian(op[2..]);
            if (version != Version)
            {
                _log($"usbip: unsupported protocol version 0x{version:X4}");
                client.Dispose();
                return;
            }

            if (code == OpReqDevlist)
            {
                var reply = new List<byte>();
                reply.AddRange(OpHeader(OpRepDevlist, 0));
                reply.AddRange(BeU32(1));
                reply.AddRange(DeviceRecord());
                reply.AddRange([0x01, 0x01, 0x00, 0, 0x01, 0x02, 0x00, 0]); // AudioControl, AudioStreaming
                stream.Write(reply.ToArray());
                client.Dispose();
                return;
            }

            if (code != OpReqImport)
            {
                _log($"usbip: unsupported op 0x{code:X4}");
                client.Dispose();
                return;
            }

            var busRaw = new byte[32];
            stream.ReadExactly(busRaw);
            string busId = Encoding.ASCII.GetString(busRaw).TrimEnd('\0');
            if (busId != BusId)
            {
                stream.Write(OpHeader(OpRepImport, 1));
                _log($"usbip: import of unknown bus id '{busId}' refused");
                client.Dispose();
                return;
            }
            stream.Write([.. OpHeader(OpRepImport, 0), .. DeviceRecord()]);
            client.ReceiveTimeout = 0;
            string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";

            var session = new Session(this, client);
            lock (_gate)
            {
                if (_disposed) { client.Dispose(); return; }
                // Single session: stop the previous one completely (reader + iso thread) before
                // resetting shared device/clock state, so it can no longer touch them.
                var old = _session;
                _session = null;
                old?.Close();
                old?.Done.Wait();
                _device.Reset();
                Clock.Reset();
                _session = session;
            }
            try
            {
                Stats.Session();
                _log($"usbip: session started ({remote})");
                session.Run();
            }
            finally
            {
                session.Close();
                session.Done.Set(); // also when Run() never started, so a later replacement cannot hang
                lock (_gate) if (_session == session) _session = null;
                _log("usbip: session ended");
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or EndOfStreamException)
        {
            client.Dispose();
        }
    }

    static byte[] OpHeader(ushort code, uint status)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(b, Version);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), code);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), status);
        return b;
    }

    static byte[] BeU32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    /// <summary>struct usbip_usb_device (312 bytes).</summary>
    static byte[] DeviceRecord()
    {
        var b = new byte[312];
        Encoding.ASCII.GetBytes("/sys/devices/platform/volumebridge/" + BusId).CopyTo(b, 0);
        Encoding.ASCII.GetBytes(BusId).CopyTo(b, 256);
        var d = SpeakerDescriptors.Device;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(288), 1); // busnum
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(292), 1); // devnum
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(296), 2); // USB_SPEED_FULL
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(300), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8)));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(302), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(10)));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(304), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(12)));
        b[306] = d[4]; b[307] = d[5]; b[308] = d[6];
        b[309] = 1;                                   // bConfigurationValue
        b[310] = 1;                                   // bNumConfigurations
        b[311] = SpeakerDescriptors.InterfaceCount;   // bNumInterfaces
        return b;
    }

    sealed class Session(UsbipServer server, TcpClient client)
    {
        sealed record Pending(uint Seq, long Deadline, byte[] Reply);

        readonly NetworkStream _stream = client.GetStream();
        readonly object _write = new();
        readonly object _q = new();
        readonly LinkedList<Pending> _queue = new();
        readonly CancellationTokenSource _cts = new();
        readonly object _closeGate = new();
        bool _closed;

        /// <summary>Set once both the reader and the iso completion thread have exited.</summary>
        public ManualResetEventSlim Done { get; } = new();

        public void Run()
        {
            var scheduler = new Thread(CompleteIsoUrbs) { IsBackground = true, Name = "usbip-iso", Priority = ThreadPriority.Highest };
            scheduler.Start();
            try
            {
                var header = new byte[48];
                while (!_cts.IsCancellationRequested)
                {
                    _stream.ReadExactly(header);
                    uint command = BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (command == CmdSubmit) Submit(header);
                    else if (command == CmdUnlink) Unlink(header);
                    else throw new IOException($"unsupported URB command {command}");
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or EndOfStreamException)
            {
                if (!_cts.IsCancellationRequested) server._log($"usbip: connection lost: {e.Message}");
            }
            finally
            {
                Close();
                scheduler.Join();
                lock (_closeGate) _cts.Dispose(); // Close() never touches _cts after _closed is set
                Done.Set();
            }
        }

        public void Close()
        {
            lock (_closeGate)
            {
                if (_closed) return;
                _closed = true;
                _cts.Cancel();
            }
            client.Dispose();
            lock (_q) { _queue.Clear(); Monitor.PulseAll(_q); }
        }

        void Submit(byte[] h)
        {
            uint seq = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(4));
            uint dir = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(12));
            uint ep = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(16));
            int length = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(24));
            uint startFrame = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(28));
            uint packetCount = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(32));
            bool iso = packetCount != NoIso && packetCount != 0;

            if (length < 0 || length > MaxOutBytes) throw new IOException($"bad transfer length {length}");
            if (iso && packetCount > MaxIsoPackets) throw new IOException($"bad iso packet count {packetCount}");

            byte[] outData = [];
            if (dir == DirOut && length > 0)
            {
                outData = new byte[length];
                _stream.ReadExactly(outData);
            }
            var packets = new IsoPacket[iso ? packetCount : 0];
            if (iso)
            {
                var raw = new byte[16 * packetCount];
                _stream.ReadExactly(raw);
                for (int i = 0; i < packets.Length; i++)
                {
                    var p = raw.AsSpan(16 * i);
                    packets[i] = new IsoPacket(
                        BinaryPrimitives.ReadInt32BigEndian(p),
                        BinaryPrimitives.ReadInt32BigEndian(p[4..]));
                }
            }

            if (ep == 0)
            {
                server.Stats.Control();
                var setup = SetupPacket.Parse(h.AsSpan(40, 8));
                var (data, status) = server._device.HandleControl(setup, outData);
                if (data.Length > length) data = data[..length];
                bool isOut = dir == DirOut;
                int actual = status != 0 ? 0 : isOut ? outData.Length : data.Length;
                Write(RetSubmitFrame(seq, status, actual, 0, isOut || status != 0 ? [] : data, null));
                if (!server._device.PlaybackActive) server.Clock.Reset(); // next stream starts from "now"
                return;
            }

            if (ep == SpeakerDescriptors.OutEndpoint && dir == DirOut && iso)
            {
                bool active = server._device.PlaybackActive;
                int frames = 0, actualTotal = 0;
                for (int i = 0; i < packets.Length; i++)
                {
                    var (offset, len) = (packets[i].Offset, packets[i].Length);
                    bool valid = offset >= 0 && len >= 0 && (long)offset + len <= outData.Length;
                    packets[i] = packets[i] with { Actual = valid ? len : 0, Status = valid ? 0 : -22 };
                    if (!valid || len == 0) continue;
                    actualTotal += len;
                    if (active) server._sink(outData.AsSpan(offset, len));
                    frames += len / SpeakerDescriptors.BytesPerFrame;
                }
                server.Stats.IsoUrb(frames);
                long deadline = server.Clock.Reserve(packets.Length, Stopwatch.GetTimestamp());
                var reply = RetSubmitFrame(seq, 0, actualTotal, startFrame, [], packets);
                lock (_q)
                {
                    _queue.AddLast(new Pending(seq, deadline, reply));
                    Monitor.Pulse(_q);
                }
                return;
            }

            for (int i = 0; i < packets.Length; i++) packets[i] = packets[i] with { Actual = 0, Status = SpeakerDevice.Stall };
            Write(RetSubmitFrame(seq, SpeakerDevice.Stall, 0, startFrame, [], iso ? packets : null));
        }

        void Unlink(byte[] h)
        {
            uint seq = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(4));
            uint target = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(20));
            bool found = false;
            lock (_q)
            {
                for (var n = _queue.First; n is not null; n = n.Next)
                {
                    if (n.Value.Seq != target) continue;
                    _queue.Remove(n);
                    found = true;
                    break;
                }
                if (found && _queue.Count == 0) server.Clock.Reset();
            }
            server.Stats.Unlink();
            var b = new byte[48];
            BinaryPrimitives.WriteUInt32BigEndian(b, RetUnlink);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), seq);
            BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), found ? EConnReset : 0);
            Write(b);
        }

        void CompleteIsoUrbs()
        {
            var ct = _cts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    Pending p;
                    lock (_q)
                    {
                        while (_queue.Count == 0 && !ct.IsCancellationRequested) Monitor.Wait(_q, 100);
                        if (ct.IsCancellationRequested) return;
                        p = _queue.First!.Value;
                    }
                    if (!server._waiter.WaitUntil(p.Deadline, ct)) return;
                    lock (_q)
                    {
                        if (!ReferenceEquals(_queue.First?.Value, p)) continue; // unlinked meanwhile
                        _queue.RemoveFirst();
                    }
                    server.Stats.Late(Stopwatch.GetTimestamp() - p.Deadline);
                    Write(p.Reply);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                Close();
            }
        }

        void Write(byte[] frame)
        {
            lock (_write) _stream.Write(frame);
        }

        static byte[] RetSubmitFrame(uint seq, int status, int actual, uint startFrame, byte[] data, IsoPacket[]? packets)
        {
            var b = new byte[48 + data.Length + 16 * (packets?.Length ?? 0)];
            BinaryPrimitives.WriteUInt32BigEndian(b, RetSubmit);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), seq);
            BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), status);
            BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(24), actual);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(28), packets is null ? 0 : startFrame);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(32), packets is null ? NoIso : (uint)packets.Length);
            int errors = packets?.Count(p => p.Status != 0) ?? 0;
            BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(36), errors);
            data.CopyTo(b, 48);
            int o = 48 + data.Length;
            foreach (var p in packets ?? [])
            {
                BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(o), p.Offset);
                BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(o + 4), p.Length);
                BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(o + 8), p.Actual);
                BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(o + 12), p.Status);
                o += 16;
            }
            return b;
        }
    }

    readonly record struct IsoPacket(int Offset, int Length, int Actual = 0, int Status = 0);
}
