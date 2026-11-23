using NAudio.Wave;
using VolumeBridge.Core;
using VolumeBridge.Core.Dsp;

namespace VolumeBridge.Audio;

static class WaveFormats
{
    static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");

    public static SampleKind? KindOf(WaveFormat f)
    {
        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (f is WaveFormatExtensible x && x.SubFormat == IeeeFloat);
        bool isPcm = f.Encoding == WaveFormatEncoding.Pcm || (f is WaveFormatExtensible y && y.SubFormat == Pcm);
        return (isFloat, isPcm, f.BitsPerSample) switch
        {
            (true, _, 32) => SampleKind.Float32,
            (_, true, 16) => SampleKind.Pcm16,
            (_, true, 24) => SampleKind.Pcm24,
            (_, true, 32) => SampleKind.Pcm32,
            _ => null,
        };
    }

    /// <summary>Null when unsupported or inconsistent (the render buffer size is derived from it).</summary>
    public static OutputFormat? ToOutput(WaveFormat f)
    {
        OutputFormat? fmt = KindOf(f) switch
        {
            SampleKind.Float32 => new OutputFormat(f.SampleRate, f.Channels, SampleFormat.Float32),
            SampleKind.Pcm16 => new OutputFormat(f.SampleRate, f.Channels, SampleFormat.Int16),
            SampleKind.Pcm24 => new OutputFormat(f.SampleRate, f.Channels, SampleFormat.Int24),
            SampleKind.Pcm32 => new OutputFormat(f.SampleRate, f.Channels, SampleFormat.Int32),
            _ => null,
        };
        return fmt is { } o && o.SampleRate > 0 && o.Channels > 0 && f.BlockAlign == o.BytesPerFrame ? o : null;
    }
}
