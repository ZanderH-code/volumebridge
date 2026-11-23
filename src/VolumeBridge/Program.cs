using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using VolumeBridge.Audio;
using VolumeBridge.Core;
using VolumeBridge.Ui;

namespace VolumeBridge;

static class Program
{
    const string Usage = """
        VolumeBridge - make the Windows volume slider and mute control a fixed-volume USB speaker.

        Usage:
          VolumeBridge                          Open the window (closing it keeps running in the tray)
          VolumeBridge --tray                   Start in the tray (used by autostart)
          VolumeBridge --apo-setup [endpointId] (admin) Add the volume plugin to the speaker(s)
          VolumeBridge --apo-remove             (admin) Remove the plugin and restore the original settings
          VolumeBridge --install-autostart      Start at logon (Task Scheduler, current user)
          VolumeBridge --uninstall-autostart
          VolumeBridge --open-logs              Open the folder with config.json and the logs
          VolumeBridge --help
        """;

    const string MutexName = @"Global\VolumeBridge.Running";
    const string ShowEventName = @"Local\VolumeBridge.Show";

    [STAThread]
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
        return Run(args);
    }

    // AssemblyResolve can fire more than once for the same name (e.g. from VolumeBridge and from NAudio.Wasapi);
    // loading the bytes twice would create two copies of every NAudio type, so each assembly is loaded once.
    static readonly Dictionary<string, Assembly> EmbeddedAssemblies = new(StringComparer.OrdinalIgnoreCase);

    static Assembly? ResolveEmbeddedAssembly(object? sender, ResolveEventArgs args)
    {
        string? simple = new AssemblyName(args.Name).Name;
        if (string.IsNullOrEmpty(simple)) return null;
        lock (EmbeddedAssemblies)
        {
            if (EmbeddedAssemblies.TryGetValue(simple!, out var loaded)) return loaded;
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(simple + ".dll");
            if (s is null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var asm = Assembly.Load(ms.ToArray());
            EmbeddedAssemblies[simple!] = asm;
            return asm;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run(string[] args)
    {
        string mode = args.Length == 0 ? "--gui" : args[0];
        bool gui = mode is "--gui" or "--tray" or "--updated";

        if (!gui)
        {
            // WinExe: print to the parent console when started from a terminal. The plugin setup
            // commands are also run hidden (installer, Enable button) and log to a file instead.
            bool console = AttachConsole(-1) || (mode is not ("--apo-setup" or "--apo-remove") && AllocConsole());
            if (console)
            {
                try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }
            }
        }

        string dataDir = AppPaths.DataDir();
        Directory.CreateDirectory(dataDir);

        switch (mode)
        {
            case "--help" or "-h":
                Console.WriteLine(Usage);
                return 0;
            case "--open-logs":
                Process.Start(new ProcessStartInfo(dataDir) { UseShellExecute = true });
                return 0;
            case "--install-autostart":
                return Try(() => Autostart.Install(Application.ExecutablePath), "autostart installed");
            case "--uninstall-autostart":
                return Try(Autostart.Uninstall, "autostart removed");
            case "--apo-setup" or "--apo-remove":
                return ApoAdmin(mode, args, dataDir);
            case "--gui" or "--tray" or "--updated":
                break;
            default:
                Console.Error.WriteLine($"Unknown option: {mode}\n\n{Usage}");
                return 2;
        }

        // One instance per machine. After a self-update (--updated), the old process is still exiting.
        Mutex? single = null;
        bool first;
        try
        {
            single = new Mutex(false, MutexName);
            int waitMs = mode == "--updated" ? 15_000 : 0;
            try { first = single.WaitOne(waitMs); }
            catch (AbandonedMutexException) { first = true; }
        }
        catch (UnauthorizedAccessException) { first = false; } // held by an elevated instance
        if (!first)
        {
            // Bring the running window to the front, except for the (delayed) logon task, which must stay hidden.
            if (mode != "--tray")
            {
                try
                {
                    using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                    show.Set();
                }
                catch (UnauthorizedAccessException) { /* running instance is elevated */ }
            }
            single?.Dispose();
            return 0;
        }

        string configPath = Path.Combine(dataDir, AppConfig.FileName);
        AppConfig config;
        try { config = AppConfig.LoadOrCreate(configPath); }
        catch (Exception e)
        {
            MessageBox.Show($"Cannot read {configPath}: {e.Message}", "VolumeBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        Loc.Current = Loc.Resolve(config.Language, CultureInfo.CurrentUICulture);

        int exitCode = RunGui(mode, config, dataDir, configPath);
        try { single?.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
        single?.Dispose();
        return exitCode;
    }

    static int RunGui(string mode, AppConfig config, string dataDir, string configPath)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var log = new FileLog(Path.Combine(dataDir, "volumebridge.log"));
        log.Write($"VolumeBridge {TrayApp.CurrentVersion.ToString(3)} {mode}, {Environment.OSVersion}, config: {configPath}");
        using var apo = new ApoController(config, log.Write);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        string? notice = mode == "--updated"
            ? Loc.T($"已更新到 {TrayApp.CurrentVersion.ToString(3)}", $"Updated to {TrayApp.CurrentVersion.ToString(3)}")
            : null;
        Application.ThreadException += (_, e) => log.Write($"UI error: {e.Exception}");
        using var app = new TrayApp(config, apo, log, dataDir, configPath, show, startHidden: mode == "--tray", notice);
        Application.Run(app);
        return 0;
    }

    /// <summary>
    /// Elevated plugin setup/removal (run by the installer, the uninstaller and the window's "Enable" button).
    /// Logs to %ProgramData%\VolumeBridge\apo-setup.log.
    /// </summary>
    static int ApoAdmin(string mode, string[] args, string dataDir)
    {
        string logDir = Path.GetDirectoryName(ApoShared.FilePath)!;
        Directory.CreateDirectory(logDir);
        using var log = new FileLog(Path.Combine(logDir, "apo-setup.log"), append: true) { Echo = Console.WriteLine };
        // Whatever goes wrong must still leave a trace.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Write($"{mode} crashed: {e.ExceptionObject}");
        try
        {
            if (ApoRegistration.IsLegacyInstallerPresent())
            {
                // The old installed version owns %ProgramFiles%\VolumeBridge; its own uninstaller restores everything.
                log.Write($"refusing {mode}: an older installed version of VolumeBridge (Inno Setup) is still present. Uninstall it in Settings -> Apps first.");
                return 2;
            }
            if (mode == "--apo-remove")
            {
                ApoRegistration.FullRemove(log.Write);
                return 0;
            }
            string configPath = Path.Combine(dataDir, AppConfig.FileName);
            var config = File.Exists(configPath) ? AppConfig.LoadOrCreate(configPath) : new AppConfig();
            List<string> ids = args.Length > 1 ? [args[1]] : FindSpeakers(config);
            log.Write($"apo-setup for {ids.Count} endpoint(s): {string.Join(", ", ids)}");
            if (ids.Count == 0)
            {
                log.Write("no matching speaker connected; nothing changed (enable it later from the VolumeBridge window)");
                return 0;
            }
            string dllPath = ApoRegistration.DeployAndRegisterDll(log.Write);
            ApoRegistration.Install(ids, log.Write);
            ApoRegistration.CleanupOldDlls(keepPath: dllPath, removeDirIfEmpty: false, log.Write);
            return 0;
        }
        catch (Exception e)
        {
            log.Write($"{mode} failed: {e}");
            return 1;
        }
    }

    /// <summary>All active playback devices matching the configured speaker.</summary>
    static List<string> FindSpeakers(AppConfig config)
    {
        using var e = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var list = e.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active).ToList();
        try
        {
            var infos = list.Select(d => new DeviceInfo(d.ID, d.FriendlyName)).ToList();
            return DeviceMatcher.AllMatches(config.OutputKeywords, config.OutputDeviceId, infos).Select(d => d.Id).ToList();
        }
        finally { foreach (var d in list) d.Dispose(); }
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

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}
