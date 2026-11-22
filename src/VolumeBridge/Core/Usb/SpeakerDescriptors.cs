using System.Text;

namespace VolumeBridge.Core.Usb;

/// <summary>
/// USB Audio Class 1.0 descriptors for a playback-only, full-speed, 48 kHz / 16-bit / stereo speaker
/// with a master volume+mute Feature Unit. Adapted from Virtual Cables (BSD-2, tarekwasfy01/Virtual-Cables,
/// src/internal/uac1/descriptors.go) with the capture path removed.
/// </summary>
public static class SpeakerDescriptors
{
    public const ushort VendorId = 0xFFFF;
    public const ushort ProductId = 0x5642;
    public const string Manufacturer = "VolumeBridge";
    public const string Product = "VolumeBridge Speaker";
    public const string Serial = "VOLBRIDGE001";

    public const byte FeatureUnitId = 2;
    public const byte StreamingInterface = 1;
    public const byte OutEndpoint = 0x01;
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BytesPerFrame = 4;
    public const int MaxPacketSize = SampleRate / 1000 * BytesPerFrame; // 192
    public const byte InterfaceCount = 2;

    public const byte TypeDevice = 0x01, TypeConfiguration = 0x02, TypeString = 0x03,
        TypeInterface = 0x04, TypeEndpoint = 0x05, TypeCsInterface = 0x24, TypeCsEndpoint = 0x25;

    public static readonly byte[] Device =
    [
        18, TypeDevice,
        0x10, 0x01,             // bcdUSB 1.10: full speed, no qualifier/BOS needed
        0x00, 0x00, 0x00,       // class defined per interface
        64,                     // EP0 max packet
        VendorId & 0xFF, VendorId >> 8,
        ProductId & 0xFF, ProductId >> 8,
        0x01, 0x00,             // bcdDevice
        1, 2, 3,                // iManufacturer, iProduct, iSerialNumber
        1,                      // bNumConfigurations
    ];

    public static readonly byte[] Configuration = BuildConfiguration();

    static byte[] BuildConfiguration()
    {
        var b = new List<byte>();
        void Add(params byte[] x) => b.AddRange(x);

        Add(9, TypeConfiguration, 0, 0, InterfaceCount, 1, 0, 0x80, 50); // wTotalLength patched below

        // Interface 0: AudioControl.
        Add(9, TypeInterface, 0, 0, 0, 0x01, 0x01, 0x00, 0);
        // AC header: bcdADC 1.00, wTotalLength 40 (= 9 + 12 + 10 + 9), one streaming interface (1).
        Add(9, TypeCsInterface, 0x01, 0x00, 0x01, 40, 0, 1, StreamingInterface);
        // Input terminal 1: USB streaming, 2 channels (L, R).
        Add(12, TypeCsInterface, 0x02, 1, 0x01, 0x01, 0, Channels, 0x03, 0x00, 0, 0);
        // Feature unit 2 <- terminal 1: master controls = mute | volume; no per-channel controls.
        Add(10, TypeCsInterface, 0x06, FeatureUnitId, 1, 1, 0x03, 0x00, 0x00, 0);
        // Output terminal 3: speaker <- unit 2.
        Add(9, TypeCsInterface, 0x03, 3, 0x01, 0x03, 0, FeatureUnitId, 0);

        // Interface 1 alt 0: zero bandwidth.
        Add(9, TypeInterface, StreamingInterface, 0, 0, 0x01, 0x02, 0x00, 0);
        // Interface 1 alt 1: PCM 48 kHz / 16 bit / stereo on isochronous adaptive OUT endpoint 1.
        Add(9, TypeInterface, StreamingInterface, 1, 1, 0x01, 0x02, 0x00, 0);
        Add(7, TypeCsInterface, 0x01, 1, 1, 0x01, 0x00);
        Add(11, TypeCsInterface, 0x02, 1, Channels, 2, 16, 1, 0x80, 0xBB, 0x00);
        Add(9, TypeEndpoint, OutEndpoint, 0x09, MaxPacketSize & 0xFF, MaxPacketSize >> 8, 1, 0, 0);
        Add(7, TypeCsEndpoint, 0x01, 0x00, 0, 0, 0);

        b[2] = (byte)b.Count;
        b[3] = (byte)(b.Count >> 8);
        return b.ToArray();
    }

    public static byte[]? Get(byte type, byte index) => (type, index) switch
    {
        (TypeDevice, 0) => Device,
        (TypeConfiguration, 0) => Configuration,
        (TypeString, 0) => [4, TypeString, 0x09, 0x04], // en-US
        (TypeString, 1) => StringDescriptor(Manufacturer),
        (TypeString, 2) => StringDescriptor(Product),
        (TypeString, 3) => StringDescriptor(Serial),
        _ => null,
    };

    static byte[] StringDescriptor(string s)
    {
        var utf16 = Encoding.Unicode.GetBytes(s);
        var b = new byte[2 + utf16.Length];
        b[0] = (byte)b.Length;
        b[1] = TypeString;
        utf16.CopyTo(b, 2);
        return b;
    }
}
