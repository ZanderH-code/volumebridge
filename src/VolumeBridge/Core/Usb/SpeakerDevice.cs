using System.Buffers.Binary;

namespace VolumeBridge.Core.Usb;

public readonly record struct SetupPacket(byte RequestType, byte Request, ushort Value, ushort Index, ushort Length)
{
    public static SetupPacket Parse(ReadOnlySpan<byte> b) => new(
        b[0], b[1],
        BinaryPrimitives.ReadUInt16LittleEndian(b[2..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[4..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[6..]));

    public override string ToString() =>
        $"bmRequestType=0x{RequestType:X2} bRequest=0x{Request:X2} wValue=0x{Value:X4} wIndex=0x{Index:X4} wLength={Length}";
}

/// <summary>Volume range advertised to Windows, in UAC1 units of 1/256 dB.</summary>
public readonly record struct VolumeRange(short Min, short Max, short Res)
{
    /// <summary>Builds a range Windows accepts: res ≥ 1, (max − min) divisible by res, min &lt; max ≤ 0 dB.</summary>
    public static VolumeRange FromDb(double minDb, double maxDb, double stepDb)
    {
        int res = Math.Clamp((int)Math.Round(stepDb * 256), 1, 256 * 10);
        int floor = (short.MinValue + 1) / res * res; // most negative multiple of res that is not 0x8000 (silence)
        int Snap(double db) => Math.Clamp((int)Math.Round(db * 256 / res) * res, floor, 0);
        int max = Snap(maxDb), min = Snap(minDb);
        if (min >= max)
        {
            if (max - res >= floor) min = max - res;
            else { min = floor; max = floor + res; }
        }
        return new VolumeRange((short)min, (short)max, (short)res);
    }

    public short Clamp(int raw) => (short)Math.Clamp(raw, Min, Max);
}

/// <summary>
/// Control-endpoint behaviour of the emulated speaker (standard requests during enumeration plus
/// UAC1 sampling-frequency and Feature Unit mute/volume requests). Thread-safe.
/// </summary>
public sealed class SpeakerDevice(VolumeRange range)
{
    public const int Stall = -32; // -EPIPE

    /// <summary>UAC1 "-infinity dB" volume value.</summary>
    public const short VolumeSilence = unchecked((short)0x8000);

    const byte GetStatus = 0x00, ClearFeature = 0x01, SetFeature = 0x03, SetAddress = 0x05, GetDescriptor = 0x06,
        GetConfiguration = 0x08, SetConfiguration = 0x09, GetInterface = 0x0A, SetInterface = 0x0B, SynchFrame = 0x0C;
    const byte SetCur = 0x01, GetCur = 0x81, GetMin = 0x82, GetMax = 0x83, GetRes = 0x84;
    const byte MuteControl = 0x01, VolumeControl = 0x02, SamplingFreqControl = 0x01;

    readonly object _gate = new();
    byte _configuration;
    byte _streamingAlt;
    bool _mute;
    short _volume = range.Clamp(-20 * 256); // safe initial level for a newly seen endpoint

    public VolumeRange Range { get; } = range;

    /// <summary>Raised (on the USB/IP session thread) after Windows changes volume or mute.</summary>
    public event Action<short, bool>? VolumeChanged;

    public bool PlaybackActive { get { lock (_gate) return _configuration == 1 && _streamingAlt == 1; } }
    public (short Volume, bool Mute) VolumeState { get { lock (_gate) return (_volume, _mute); } }

    /// <summary>Linear gain for a UAC1 volume (1/256 dB) and mute state.</summary>
    public static double Gain(short volume, bool mute) =>
        mute || volume == VolumeSilence ? 0.0 : Math.Pow(10, volume / 256.0 / 20.0);

    /// <summary>Back to the unconfigured state (new session / bus reset).</summary>
    public void Reset()
    {
        lock (_gate) { _configuration = 0; _streamingAlt = 0; }
    }

    /// <summary>Handles one control transfer. Returns IN data (may be empty) and a USB/IP status (0 or <see cref="Stall"/>).</summary>
    public (byte[] Data, int Status) HandleControl(SetupPacket s, ReadOnlySpan<byte> outData)
    {
        (byte[] Data, int Status) result;
        bool changed = false;
        short vol;
        bool mute;
        lock (_gate)
        {
            result = (s.RequestType & 0x60) switch
            {
                0x00 => Standard(s),
                0x20 when s.RequestType == PortRequestType => PortRequest(s),
                0x20 => Class(s, outData, ref changed),
                _ => Fail(), // vendor
            };
            vol = _volume;
            mute = _mute;
        }
        if (changed) VolumeChanged?.Invoke(vol, mute);
        if (result.Data.Length > s.Length) result.Data = result.Data[..s.Length];
        return result;
    }

    static readonly byte[] None = [];
    static (byte[], int) Ok(params byte[] data) => (data, 0);
    static (byte[], int) Fail() => (None, Stall);

    /// <summary>Hub-port class request (OUT | CLASS | OTHER) that usbip-win2 sends on device reset.</summary>
    const byte PortRequestType = 0x23;
    const ushort PortFeatureReset = 4;

    (byte[], int) PortRequest(SetupPacket s)
    {
        // Post-enumeration reset (IOCTL_INTERNAL_USB_RESET_PORT): the host keeps its configuration and
        // alternate settings and does not re-send them, so the device state must survive.
        return s.Request == SetFeature && s.Value == PortFeatureReset ? Ok() : Fail();
    }

    (byte[], int) Standard(SetupPacket s)
    {
        switch (s.Request)
        {
            case GetDescriptor:
                var d = SpeakerDescriptors.Get((byte)(s.Value >> 8), (byte)s.Value);
                return d is null ? Fail() : Ok(d);
            case SetAddress:
            case ClearFeature:
            case SetFeature:
                return Ok();
            case SetConfiguration:
                if (s.Value > 1) return Fail();
                _configuration = (byte)s.Value;
                _streamingAlt = 0;
                return Ok();
            case GetConfiguration:
                return Ok(_configuration);
            case SetInterface:
                if (_configuration != 1) return Fail();
                if (s.Index == 0 && s.Value == 0) return Ok();
                if (s.Index == SpeakerDescriptors.StreamingInterface && s.Value <= 1)
                {
                    _streamingAlt = (byte)s.Value;
                    return Ok();
                }
                return Fail();
            case GetInterface:
                if (s.Index == 0) return Ok(0);
                if (s.Index == SpeakerDescriptors.StreamingInterface) return Ok(_streamingAlt);
                return Fail();
            case GetStatus:
                return Ok(0, 0);
            case SynchFrame:
                return Ok(0, 0);
            default:
                return Fail();
        }
    }

    (byte[], int) Class(SetupPacket s, ReadOnlySpan<byte> outData, ref bool changed)
    {
        int recipient = s.RequestType & 0x1F;
        byte selector = (byte)(s.Value >> 8);

        // Endpoint SAMPLING_FREQ_CONTROL on the OUT endpoint: only 48 kHz exists.
        if (recipient == 2 && selector == SamplingFreqControl && (byte)s.Index == SpeakerDescriptors.OutEndpoint)
        {
            return s.Request switch
            {
                SetCur => outData.Length >= 3 && (outData[0] | outData[1] << 8 | outData[2] << 16) != SpeakerDescriptors.SampleRate
                    ? Fail() : Ok(),
                GetCur or GetMin or GetMax => Ok(0x80, 0xBB, 0x00),
                GetRes => Ok(1, 0, 0),
                _ => Fail(),
            };
        }

        // Feature Unit 2 on interface 0, master channel (0) only.
        byte channel = (byte)s.Value;
        if (recipient != 1 || (byte)s.Index != 0 || (byte)(s.Index >> 8) != SpeakerDescriptors.FeatureUnitId || channel != 0)
            return Fail();

        switch (selector, s.Request)
        {
            case (MuteControl, SetCur):
                if (outData.Length < 1) return Fail();
                changed = _mute != (outData[0] != 0);
                _mute = outData[0] != 0;
                return Ok();
            case (MuteControl, GetCur):
                return Ok(_mute ? (byte)1 : (byte)0);
            case (VolumeControl, SetCur):
                if (outData.Length < 2) return Fail();
                short raw = BinaryPrimitives.ReadInt16LittleEndian(outData);
                short v = raw == VolumeSilence ? VolumeSilence : Range.Clamp(raw);
                changed = _volume != v;
                _volume = v;
                return Ok();
            case (VolumeControl, GetCur): return Ok(Le(_volume));
            case (VolumeControl, GetMin): return Ok(Le(Range.Min));
            case (VolumeControl, GetMax): return Ok(Le(Range.Max));
            case (VolumeControl, GetRes): return Ok(Le(Range.Res));
            default: return Fail();
        }
    }

    static byte[] Le(short v) => [(byte)v, (byte)(v >> 8)];
}
