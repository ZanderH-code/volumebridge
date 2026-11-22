using System.Text;
using VolumeBridge.Audio;
using VolumeBridge.Core;

namespace VolumeBridge;

static class Program
{
    const string Usage = """
        VolumeBridge - make Windows volume control a fixed-volume USB speaker.

        Usage:
          VolumeBridge --probe [--seconds N]   Phase 1: print devices, volume, mute, format, capture level every 200 ms
          VolumeBridge --help

        config.json and probe.log live next to the exe. The audio engine is not implemented yet (see docs/PLAN.md).
        """;

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string baseDir = AppContext.BaseDirectory;

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        if (args[0] != "--probe")
        {
            Console.Error.WriteLine($"Unknown option: {args[0]}\n\n{Usage}");
            return 2;
        }

        TimeSpan? duration = null;
        int i = Array.IndexOf(args, "--seconds");
        if (i >= 0)
        {
            if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int secs) || secs <= 0)
            {
                Console.Error.WriteLine("--seconds needs a positive integer");
                return 2;
            }
            duration = TimeSpan.FromSeconds(secs);
        }

        AppConfig config;
        try { config = AppConfig.LoadOrCreate(Path.Combine(baseDir, AppConfig.FileName)); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Cannot read {AppConfig.FileName}: {e.Message}");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        using var log = new StreamWriter(Path.Combine(baseDir, "probe.log"), append: false, Encoding.UTF8) { AutoFlush = true };
        using var tee = new TeeWriter(Console.Out, log);
        tee.WriteLine($"VolumeBridge probe, {Environment.OSVersion}, config: {Path.Combine(baseDir, AppConfig.FileName)}. Ctrl+C to stop.");
        return new Probe(config, tee).Run(duration, cts.Token);
    }

    sealed class TeeWriter(TextWriter a, TextWriter b) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { a.Write(value); b.Write(value); }
        public override void Write(string? value) { a.Write(value); b.Write(value); }
        public override void WriteLine(string? value) { a.WriteLine(value); b.WriteLine(value); }
    }
}
