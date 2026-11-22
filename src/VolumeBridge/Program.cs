using System.Runtime.InteropServices;
using System.Text;
using VolumeBridge.Audio;
using VolumeBridge.Core;

namespace VolumeBridge;

static class Program
{
    const string Usage = """
        VolumeBridge - make Windows volume control a fixed-volume USB speaker.

        Usage:
          VolumeBridge --speaker               M1: expose "VolumeBridge Speaker" via usbip-win2 and log volume/mute/level
          VolumeBridge --probe [--seconds N]   Print Windows audio devices, volume, mute, format every 200 ms
          VolumeBridge --help

        config.json, volumebridge.log and probe.log live next to the exe.
        Audio is not yet forwarded to the speaker (see docs/PLAN.md, M2).
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

        if (args[0] != "--probe" && args[0] != "--speaker")
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
        // First Ctrl+C stops cleanly; a second one force-exits if teardown hangs.
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            if (cts.IsCancellationRequested) return;
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        // Closing the console window / logoff / shutdown arrive as SIGTERM. Windows kills the process
        // once the handler returns (or after ~5 s), so block until the run loop has cleaned up
        // (detaching the virtual speaker); otherwise usbip-win2 would start re-attach attempts.
        using var finished = new ManualResetEventSlim();
        using var onTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            cts.Cancel();
            finished.Wait(4000);
        });

        bool speaker = args[0] == "--speaker";
        using var log = new StreamWriter(Path.Combine(baseDir, speaker ? "volumebridge.log" : "probe.log"), append: false, Encoding.UTF8) { AutoFlush = true };
        using var tee = new TeeWriter(Console.Out, log);
        var version = typeof(Program).Assembly.GetName().Version;
        tee.WriteLine($"VolumeBridge {version} {args[0]}, {Environment.OSVersion}, config: {Path.Combine(baseDir, AppConfig.FileName)}. Ctrl+C to stop.");
        try
        {
            if (!speaker) return new Probe(config, tee).Run(duration, cts.Token);
            void Log(string s) { lock (tee) tee.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }
            // One instance per machine; the installer also uses this name (AppMutex) to detect a running copy.
            using var single = new Mutex(true, @"Global\VolumeBridge.Running", out bool firstInstance);
            if (!firstInstance)
            {
                Log("VolumeBridge is already running.");
                return 6;
            }
            if (duration is not null) cts.CancelAfter(duration.Value);
            return new SpeakerMode(config, Log).Run(cts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            finished.Set();
        }
    }

    sealed class TeeWriter(TextWriter a, TextWriter b) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { a.Write(value); b.Write(value); }
        public override void Write(string? value) { a.Write(value); b.Write(value); }
        public override void WriteLine(string? value) { a.WriteLine(value); b.WriteLine(value); }
    }
}
