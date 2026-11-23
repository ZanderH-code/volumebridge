using System.IO.MemoryMappedFiles;

namespace VolumeBridge.Audio;

public enum ApoSource { None = 0, Notification = 1, App = 2 }

/// <summary>Snapshot of what the APO reports through the shared file (see apo/SharedState.h).</summary>
public sealed record ApoReport(int Instances, ApoSource Source, float TargetGain, int InitKind,
    long HeartbeatAgeMs, int SampleRate, int Channels, float MasterVolume, bool Muted, int Notifications);

/// <summary>
/// %ProgramData%\VolumeBridge\apo.dat, mapped by both this app and the APO inside audiodg.
/// Offsets mirror apo/SharedState.h (pack 4).
/// </summary>
public sealed class ApoShared : IDisposable
{
    public const uint Magic = 0x31474256; // "VBG1"
    const int Size = 4096;
    const int OMagic = 0, OVersion = 4, OAppGain = 8, OAppSeq = 12, OInstances = 16, OSource = 20, OTargetGain = 24,
        OInitKind = 28, OHeartbeat = 32, ORate = 40, OChannels = 44, OMaster = 48, OMuted = 52, ONotifs = 56;

    readonly MemoryMappedFile _map;
    readonly MemoryMappedViewAccessor _view;
    uint _seq;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VolumeBridge", "apo.dat");

    public ApoShared()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var fs = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < Size) fs.SetLength(Size);
        _map = MemoryMappedFile.CreateFromFile(fs, null, Size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        _view = _map.CreateViewAccessor(0, Size);
        if (_view.ReadUInt32(OMagic) != Magic)
        {
            _view.Write(OVersion, 1u);
            _view.Write(OMaster, -1f);
            _view.Write(OMagic, Magic);
        }
        _seq = _view.ReadUInt32(OAppSeq);
    }

    /// <summary>Publishes the gain for the APO's fallback path (used when it gets no volume notifications).</summary>
    public void WriteGain(float gain)
    {
        _view.Write(OAppGain, Math.Clamp(gain, 0f, 1f));
        _seq = _seq == uint.MaxValue ? 1 : _seq + 1;
        _view.Write(OAppSeq, _seq);
    }

    public ApoReport Read()
    {
        ulong beat = _view.ReadUInt64(OHeartbeat);
        long age = beat == 0 ? long.MaxValue : Environment.TickCount64 - (long)beat;
        return new ApoReport(
            (int)_view.ReadUInt32(OInstances),
            (ApoSource)_view.ReadUInt32(OSource),
            _view.ReadSingle(OTargetGain),
            (int)_view.ReadUInt32(OInitKind),
            age,
            (int)_view.ReadUInt32(ORate),
            (int)_view.ReadUInt32(OChannels),
            _view.ReadSingle(OMaster),
            _view.ReadUInt32(OMuted) != 0,
            (int)_view.ReadUInt32(ONotifs));
    }

    public void Dispose()
    {
        _view.Dispose();
        _map.Dispose();
    }
}
