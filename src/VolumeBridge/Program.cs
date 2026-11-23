using System.Diagnostics;
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
          VolumeBridge --open-logs             Open the folder with config.json and the logs
          VolumeBridge --help

        config.json, volumebridge.log and probe.log live next to the exe, or in %LOCALAPPDATA%\VolumeBridge
        when installed under Program Files. Audio is not yet forwarded to the speaker (docs/PLAN.md, M2).
        """;

    const string MutexName = @"Global\VolumeBridge.Running";

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        string dataDir = AppPaths.DataDir();
        Directory.CreateDirectory(dataDir);

        if (args[0] == "--open-logs")
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dataDir}\"") { UseShellExecute = true });
            return 0;
        }

        if (args[0] != "--probe" && args[0] != "--speaker")
        {
            Console.Error.WriteLine($"Unknown option: {args[0]}\n\n{Usage}");
            return 2;
        }
        bool speaker = args[0] == "--speaker";

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

        // One --speaker instance per machine, checked before touching its log file. The installer
        // uses the same name (AppMutex) to detect a running copy.
        Mutex? single = null;
        if (speaker)
        {
            bool first;
            try { single = new Mutex(true, MutexName, out first); }
            catch (UnauthorizedAccessException) { first = false; } // held by an elevated instance
            if (!first)
            {
                Console.Error.WriteLine("VolumeBridge is already running.");
                return 6;
            }
        }

        string configPath = Path.Combine(dataDir, AppConfig.FileName);
        AppConfig config;
        try { config = AppConfig.LoadOrCreate(configPath); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Cannot read {configPath}: {e.Message}");
            return 2;
        }

        // Deliberately never disposed: console/signal handlers may still run on another thread while
        // Main returns, and the process is exiting anyway.
        var cts = new CancellationTokenSource();
        var finished = new ManualResetEventSlim();

        // First Ctrl+C stops cleanly; a second one force-exits if teardown hangs.
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return;
            e.Cancel = true;
            cts.Cancel();
        };
        // Closing the console window (CTRL_CLOSE_EVENT = SIGHUP in .NET) or shutdown (SIGTERM):
        // Windows kills the process when the handler returns or after ~5 s, so block until the run
        // loop has detached the virtual speaker; otherwise usbip-win2 starts re-attach attempts.
        void OnTerminate(PosixSignalContext ctx)
        {
            ctx.Cancel = true;
            cts.Cancel();
            finished.Wait(4000);
        }
        var signals = new[]
        {
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnTerminate),
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTerminate),
        };

        int exitCode;
        using (var log = new StreamWriter(Path.Combine(dataDir, speaker ? "volumebridge.log" : "probe.log"), append: false, Encoding.UTF8) { AutoFlush = true })
        using (var tee = new TeeWriter(Console.Out, log))
        {
            var version = typeof(Program).Assembly.GetName().Version;
            tee.WriteLine($"VolumeBridge {version} {args[0]}, {Environment.OSVersion}, config: {configPath}. Ctrl+C or close the window to stop.");
            if (!speaker)
            {
                exitCode = new Probe(config, tee).Run(duration, cts.Token);
            }
            else
            {
                void Log(string s) { lock (tee) tee.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }
                if (duration is not null) cts.CancelAfter(duration.Value);
                try { exitCode = new SpeakerMode(config, Log).Run(cts.Token); }
                catch (Exception e)
                {
                    Log($"FATAL {e}");
                    exitCode = 10;
                }
            }
        }
        single?.Dispose();
        finished.Set();
        GC.KeepAlive(signals);
        return exitCode;
    }

    sealed class TeeWriter(TextWriter a, TextWriter b) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { a.Write(value); b.Write(value); }
        public override void Write(string? value) { a.Write(value); b.Write(value); }
        public override void WriteLine(string? value) { a.WriteLine(value); b.WriteLine(value); }
    }
}
