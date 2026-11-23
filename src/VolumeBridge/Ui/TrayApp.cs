using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using Microsoft.Win32;
using VolumeBridge.Audio;
using VolumeBridge.Core;
using static VolumeBridge.Core.Loc;

namespace VolumeBridge.Ui;

/// <summary>
/// GUI lifetime: tray icon + status window around one <see cref="Engine"/>. Closing the window
/// hides it; only Quit (or the end of the Windows session) stops the engine, which detaches the virtual speaker.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    /// <summary>Created by the elevated installer process once UAC is accepted (see installer InitializeSetup).</summary>
    const string UpdatingMutex = @"Global\VolumeBridge.Updating";
    static readonly TimeSpan LogEvery = TimeSpan.FromSeconds(60);

    readonly FileLog _log;
    readonly string _configPath;
    readonly NotifyIcon _tray;
    readonly ContextMenuStrip _menu = new();
    readonly ToolStripMenuItem _menuShow = new();
    readonly ToolStripMenuItem _menuLogs = new();
    readonly ToolStripMenuItem _menuQuit = new();
    readonly Icon _iconOk = AppIcon.Create(32, ok: true);
    readonly Icon _iconIdle = AppIcon.Create(32, ok: false);
    readonly MainForm _form;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    readonly RegisteredWaitHandle _showWait;
    readonly CancellationTokenSource _quit = new();
    readonly Task _engineStart;
    DateTime _lastLog = DateTime.MinValue;
    bool _hintShown;

    public TrayApp(AppConfig config, Engine? engine, ApoController? apo, FileLog log, string dataDir, string configPath,
        EventWaitHandle showSignal, bool startHidden, string? notice)
    {
        Config = config;
        Engine = engine;
        Apo = apo;
        _log = log;
        DataDir = dataDir;
        _configPath = configPath;

        _form = new MainForm(this, _iconOk); // also installs the WinForms SynchronizationContext
        _menuShow.Click += (_, _) => ShowWindow();
        _menuLogs.Click += (_, _) => Process.Start(new ProcessStartInfo(DataDir) { UseShellExecute = true });
        _menuQuit.Click += (_, _) => Quit();
        _menu.Items.AddRange([_menuShow, _menuLogs, new ToolStripSeparator(), _menuQuit]);
        _tray = new NotifyIcon { Icon = _iconIdle, Text = "VolumeBridge", ContextMenuStrip = _menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowWindow();
        ApplyTexts();

        // A second launch signals us to show the window.
        var ui = SynchronizationContext.Current!;
        _showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) =>
        {
            if (Quitting) return;
            try { ui.Post(_ => ShowWindow(), null); } catch (Exception) { /* shutting down */ }
        }, null, -1, false);

        // WM_ENDSESSION (session really ends), not WM_QUERYENDSESSION, which another app may still cancel.
        SystemEvents.SessionEnded += OnSessionEnded;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // Attach runs usbip.exe and may take a few seconds: keep it off the UI thread.
        _engineStart = Engine is null ? Task.CompletedTask : Task.Run(() => Engine.Start());
        Apo?.Start();

        if (!startHidden) ShowWindow();
        if (notice is not null) _tray.ShowBalloonTip(5000, "VolumeBridge", notice, ToolTipIcon.Info);
    }

    public AppConfig Config { get; }
    public Engine? Engine { get; }
    public ApoController? Apo { get; }
    public string DataDir { get; }
    public bool Quitting { get; private set; }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    void ApplyTexts()
    {
        _menuShow.Text = T("显示窗口", "Show window");
        _menuLogs.Text = T("打开日志文件夹", "Open log folder");
        _menuQuit.Text = T("退出", "Quit");
    }

    public void SetLanguage(UiLanguage language)
    {
        if (Current == language) return;
        Current = language;
        Config.Language = Code(language);
        SaveConfig();
        ApplyTexts();
        _form.ApplyTexts();
    }

    public void SaveConfig()
    {
        try { File.WriteAllText(_configPath, Config.ToJson()); }
        catch (Exception e) { _log.Write($"cannot save config: {e.Message}"); }
    }

    void Tick()
    {
        bool ok;
        string tip, line;
        if (Apo is not null)
        {
            var a = Apo.GetStatus();
            ok = a.State is ApoState.Working or ApoState.Idle;
            tip = ok ? "VolumeBridge: " + a.VolumeText : "VolumeBridge: " + T("需要处理", "needs attention");
            line = ApoController.StatusLine(a);
            if (_form.Visible) _form.ShowApo(a);
        }
        else
        {
            var s = Engine!.GetStatus();
            ok = s.Running && s.OutputOpen;
            tip = !s.Running ? T("VolumeBridge：未运行", "VolumeBridge: not running")
                : ok ? "VolumeBridge: " + (s.Mute ? T("静音", "muted") : s.VolumeText)
                : T("VolumeBridge：等待输出设备", "VolumeBridge: waiting for the output device");
            line = Engine.StatusLine(s);
            if (_form.Visible) _form.ShowStatus(s);
        }
        _tray.Icon = ok ? _iconOk : _iconIdle;
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        if (DateTime.UtcNow - _lastLog >= LogEvery)
        {
            _lastLog = DateTime.UtcNow;
            _log.Write(line);
        }
    }

    /// <summary>Re-reads the output selection (after the drop-down changed).</summary>
    public void ReopenOutput()
    {
        Engine?.ReopenOutput();
        Apo?.Recheck();
    }

    /// <summary>Runs the elevated plugin setup for <paramref name="endpointId"/> (one UAC prompt).</summary>
    public async Task<bool> EnableApoAsync(string endpointId)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--apo-setup \"{endpointId}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            await p.WaitForExitAsync();
            _log.Write($"apo-setup exited with {p.ExitCode}");
            Apo?.Recheck();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) // UAC declined
        {
            return false;
        }
    }

    /// <summary>Saves the new mode and restarts VolumeBridge in it.</summary>
    public void SwitchMode(bool apo)
    {
        Config.Mode = apo ? "apo" : "virtual";
        SaveConfig();
        _log.Write($"switching to mode {Config.Mode}");
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--relaunch") { UseShellExecute = false });
        Quit();
    }

    public void ShowWindow()
    {
        if (Quitting || _form.IsDisposed) return;
        if (Apo is not null) _form.ShowApo(Apo.GetStatus());
        else _form.ShowStatus(Engine!.GetStatus());
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    public void NotifyHidden()
    {
        if (_hintShown) return;
        _hintShown = true;
        _tray.ShowBalloonTip(3000, "VolumeBridge",
            T("仍在托盘中运行。右键托盘图标可退出。", "Still running in the tray. Right-click the tray icon to quit."), ToolTipIcon.Info);
    }

    void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        _log.Write("Windows session ended.");
        Quit();
    }

    public void Quit()
    {
        if (Quitting) return;
        Quitting = true;
        _quit.Cancel();
        _timer.Stop();
        // Let an in-flight Start finish first, so Stop sees (and detaches) everything it created.
        _engineStart.Wait(TimeSpan.FromSeconds(30));
        Engine?.Stop();
        _tray.Visible = false;
        if (!_form.IsDisposed) _form.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionEnded -= OnSessionEnded;
            _showWait.Unregister(null);
            _tray.Dispose();
            _timer.Dispose();
            _form.Dispose();
            _menu.Dispose();
            _iconOk.Dispose();
            _iconIdle.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// GitHub latest release → download + SHA-256 check → run the installer → quit once the installer
    /// is elevated (it then waits for us to exit, installs, and relaunches VolumeBridge).
    /// </summary>
    public async Task CheckForUpdatesAsync(IWin32Window owner, Button button)
    {
        var ct = _quit.Token;
        button.Enabled = false;
        bool Gone() => Quitting || button.IsDisposed;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("VolumeBridge-Updater");
            button.Text = T("正在检查…", "Checking…");
            var latest = await UpdateCheck.GetLatestAsync(http, ct);
            if (Gone()) return;
            if (latest is null || !UpdateCheck.IsNewer(latest.Version, CurrentVersion))
            {
                MessageBox.Show(owner, T($"已是最新版本（{CurrentVersion.ToString(3)}）。", $"You have the latest version ({CurrentVersion.ToString(3)})."),
                    "VolumeBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(owner,
                    T($"发现新版本 {latest.Tag}（当前 {CurrentVersion.ToString(3)}）。\n\n现在下载并安装吗？安装时需要管理员确认，完成后会自动重新打开。",
                      $"Version {latest.Tag} is available (you have {CurrentVersion.ToString(3)}).\n\nDownload and install it now? Installing needs administrator approval; VolumeBridge reopens afterwards."),
                    "VolumeBridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            string shown = "";
            var progress = new Progress<double>(p =>
            {
                string text = T("正在下载 ", "Downloading ") + p.ToString("P0");
                if (Gone() || text == shown) return;
                shown = text;
                button.Text = text;
            });
            string setup = await UpdateCheck.DownloadVerifiedAsync(http, latest,
                Path.Combine(Path.GetTempPath(), "VolumeBridgeUpdate"), progress, ct);
            if (Gone()) return;
            _log.Write($"update {latest.Tag} downloaded and verified: {setup}");

            // Inno's loader runs unelevated and starts an elevated child itself, so Process.Start returns
            // before UAC is answered. Quit only once the elevated installer signals it is running; if the
            // loader exits first, UAC was declined.
            button.Text = T("等待管理员确认…", "Waiting for administrator approval…");
            using var installer = Process.Start(new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true })!;
            while (!ct.IsCancellationRequested)
            {
                if (InstallerElevated())
                {
                    _log.Write("installer is running elevated; quitting for the update");
                    Quit();
                    return;
                }
                if (installer.HasExited)
                {
                    MessageBox.Show(owner, T("已取消更新。", "Update cancelled."), "VolumeBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                await Task.Delay(100, ct);
            }
        }
        catch (OperationCanceledException) when (Quitting) { }
        catch (Exception e)
        {
            _log.Write($"update failed: {e}");
            if (!Gone())
                MessageBox.Show(owner, T($"检查或下载更新失败：{e.Message}", $"Update check or download failed: {e.Message}"),
                    "VolumeBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!Gone())
            {
                button.Text = T("检查更新", "Check for updates"); // current language, even if switched meanwhile
                button.Enabled = true;
            }
        }
    }

    static bool InstallerElevated()
    {
        try
        {
            if (!Mutex.TryOpenExisting(UpdatingMutex, out var m)) return false;
            m.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; } // exists, owned by the elevated installer
    }
}
