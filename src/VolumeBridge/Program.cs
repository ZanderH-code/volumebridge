using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VolumeBridge.Audio;
using VolumeBridge.Core;
using VolumeBridge.Ui;

namespace VolumeBridge;

static partial class Program
{
    const string Usage = """
        VolumeBridge - make Windows volume control a fixed-volume USB speaker.

        Usage:
          VolumeBridge                         Open the window (closing it keeps running in the tray)
          VolumeBridge --tray                  Start in the tray (used by autostart)
          VolumeBridge --speaker               Console mode with a status line every second
          VolumeBridge --probe [--seconds N]   Print Windows audio devices, volume, mute, format
          VolumeBridge --install-autostart     Start at logon (Task Scheduler, current user)
          VolumeBridge --uninstall-autostart
          VolumeBridge --open-logs             Open the folder with config.json and the logs
          VolumeBridge --help

        config.json and logs live next to the exe, or in %LOCALAPPDATA%\VolumeBridge when installed
        under Program Files.
        """;

    const string MutexName = @"Global\VolumeBridge.Running";
    const string ShowEventName = @"Local\VolumeBridge.Show";

    [STAThread]
    static int Main(string[] args)
    {
        string mode = args.Length == 0 ? "--gui" : args[0];
        bool gui = mode is "--gui" or "--tray" or "--updated";

        if (!gui)
        {
            // WinExe: reuse the parent console when started from a terminal, otherwise open one.
            if (!AttachConsole(-1)) AllocConsole();
            Console.OutputEncoding = Encoding.UTF8;
        }

        if (mode is "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        string dataDir = AppPaths.DataDir();
        Directory.CreateDirectory(dataDir);

        switch (mode)
        {
            case "--open-logs":
                Process.Start(new ProcessStartInfo(dataDir) { UseShellExecute = true });
                return 0;
            case "--install-autostart":
                return Try(() => Autostart.Install(Environment.ProcessPath!), "autostart installed");
            case "--uninstall-autostart":
                return Try(Autostart.Uninstall, "autostart removed");
            case "--gui" or "--tray" or "--updated" or "--speaker" or "--probe":
                break;
            default:
                Console.Error.WriteLine($"Unknown option: {mode}\n\n{Usage}");
                return 2;
        }

        // One engine per machine (GUI or --speaker). The installer waits on the same mutex.
        Mutex? single = null;
        if (mode != "--probe")
        {
            bool first;
            try { single = new Mutex(true, MutexName, out first); }
            catch (UnauthorizedAccessException) { first = false; } // held by an elevated instance
            if (!first)
            {
                // Bring the running window to the front, except for the (delayed) logon task, which must stay hidden.
                if (gui && mode != "--tray")
                {
                    try
                    {
                        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                        show.Set();
                    }
                    catch (UnauthorizedAccessException) { /* running instance is elevated */ }
                }
                if (gui) return 0;
                Console.Error.WriteLine("VolumeBridge is already running.");
                return 6;
            }
        }

        string configPath = Path.Combine(dataDir, AppConfig.FileName);
        AppConfig config;
        try { config = AppConfig.LoadOrCreate(configPath); }
        catch (Exception e)
        {
            if (gui) MessageBox.Show($"Cannot read {configPath}: {e.Message}", "VolumeBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else Console.Error.WriteLine($"Cannot read {configPath}: {e.Message}");
            return 2;
        }

        Loc.Current = Loc.Resolve(config.Language, ReadInstallerLanguage(), System.Globalization.CultureInfo.CurrentUICulture);

        int exitCode = gui ? RunGui(mode, config, dataDir, configPath) : RunConsole(mode, args, config, dataDir, configPath);
        single?.Dispose();
        return exitCode;
    }

    static int RunGui(string mode, AppConfig config, string dataDir, string configPath)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var log = new FileLog(Path.Combine(dataDir, "volumebridge.log"));
        log.Write($"VolumeBridge {TrayApp.CurrentVersion.ToString(3)} {mode}, {Environment.OSVersion}, config: {configPath}");
        using var engine = new Engine(config, log.Write);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        string? notice = mode == "--updated"
            ? Loc.T($"已更新到 {TrayApp.CurrentVersion.ToString(3)}", $"Updated to {TrayApp.CurrentVersion.ToString(3)}")
            : null;
        Application.ThreadException += (_, e) => log.Write($"UI error: {e.Exception}");
        using var app = new TrayApp(engine, log, dataDir, configPath, show, startHidden: mode == "--tray", notice);
        Application.Run(app);
        engine.Stop();
        return 0;
    }

    static int RunConsole(string mode, string[] args, AppConfig config, string dataDir, string configPath)
    {
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

        // Deliberately never disposed: console/signal handlers may still run on another thread while
        // the method returns, and the process is exiting anyway.
        var cts = new CancellationTokenSource();
        var finished = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return; // a second Ctrl+C force-exits
            e.Cancel = true;
            cts.Cancel();
        };
        // Closing the console window (CTRL_CLOSE_EVENT = SIGHUP in .NET) or shutdown (SIGTERM): Windows
        // kills the process when the handler returns or after ~5 s, so wait until the virtual speaker is detached.
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
        if (duration is not null) cts.CancelAfter(duration.Value);

        int exitCode;
        if (mode == "--probe")
        {
            using var log = new StreamWriter(Path.Combine(dataDir, "probe.log"), append: false, Encoding.UTF8) { AutoFlush = true };
            using var tee = new TeeWriter(Console.Out, log);
            tee.WriteLine($"VolumeBridge probe, {Environment.OSVersion}. Ctrl+C or close the window to stop.");
            exitCode = new Probe(config, tee).Run(duration, cts.Token);
        }
        else
        {
            using var log = new FileLog(Path.Combine(dataDir, "volumebridge.log")) { Echo = Console.WriteLine };
            log.Write($"VolumeBridge {TrayApp.CurrentVersion.ToString(3)} --speaker, {Environment.OSVersion}, config: {configPath}. Ctrl+C or close the window to stop.");
            using var engine = new Engine(config, log.Write);
            try { exitCode = new SpeakerMode(engine, log.Write).Run(cts.Token); }
            catch (Exception e)
            {
                log.Write($"FATAL {e}");
                exitCode = 10;
            }
        }
        finished.Set();
        GC.KeepAlive(signals);
        return exitCode;
    }

    /// <summary>Language picked in the installer (written next to the exe), if any.</summary>
    static string? ReadInstallerLanguage()
    {
        try
        {
            string f = Path.Combine(AppContext.BaseDirectory, Loc.InstallerChoiceFile);
            return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
        }
        catch (Exception) { return null; }
    }

    static int Try(Action action, string done)
    {
        try
        {
            action();
            Console.WriteLine(done);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    sealed class TeeWriter(TextWriter a, TextWriter b) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { a.Write(value); b.Write(value); }
        public override void Write(string? value) { a.Write(value); b.Write(value); }
        public override void WriteLine(string? value) { a.WriteLine(value); b.WriteLine(value); }
    }
}
