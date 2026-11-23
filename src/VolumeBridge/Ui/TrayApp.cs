using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using Microsoft.Win32;
using VolumeBridge.Audio;
using VolumeBridge.Core;

namespace VolumeBridge.Ui;

/// <summary>
/// GUI lifetime: tray icon + status window around one <see cref="Engine"/>. Closing the window
/// hides it; only "退出" (or Windows shutdown/logoff) stops the engine, which detaches the virtual speaker.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    static readonly TimeSpan LogEvery = TimeSpan.FromSeconds(60);

    readonly FileLog _log;
    readonly string _configPath;
    readonly NotifyIcon _tray;
    readonly Icon _iconOk = AppIcon.Create(32, ok: true);
    readonly Icon _iconIdle = AppIcon.Create(32, ok: false);
    readonly MainForm _form;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    readonly EventWaitHandle _showSignal;
    readonly RegisteredWaitHandle _showWait;
    DateTime _lastLog = DateTime.MinValue;
    bool _hintShown;

    public TrayApp(Engine engine, FileLog log, string dataDir, string configPath, EventWaitHandle showSignal, bool startHidden, string? notice)
    {
        Engine = engine;
        _log = log;
        DataDir = dataDir;
        _configPath = configPath;
        _showSignal = showSignal;

        _form = new MainForm(this, _iconOk);
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => ShowWindow());
        menu.Items.Add("打开日志文件夹", null, (_, _) => Process.Start(new ProcessStartInfo(DataDir) { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Quit());
        _tray = new NotifyIcon { Icon = _iconIdle, Text = "VolumeBridge", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowWindow();

        // A second launch signals us to show the window.
        var ui = SynchronizationContext.Current!;
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => ui.Post(_ => ShowWindow(), null), null, -1, false);

        SystemEvents.SessionEnding += OnSessionEnding;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // Start the engine off the UI thread (attach runs usbip.exe and may take a moment).
        Task.Run(() => Engine.Start());

        if (!startHidden) ShowWindow();
        if (notice is not null) _tray.ShowBalloonTip(5000, "VolumeBridge", notice, ToolTipIcon.Info);
    }

    public Engine Engine { get; }
    public string DataDir { get; }
    public bool Quitting { get; private set; }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public void SaveConfig()
    {
        try { File.WriteAllText(_configPath, Engine.Config.ToJson()); }
        catch (Exception e) { _log.Write($"cannot save config: {e.Message}"); }
    }

    void Tick()
    {
        var s = Engine.GetStatus();
        bool ok = s.Running && s.OutputOpen;
        _tray.Icon = ok ? _iconOk : _iconIdle;
        string tip = !s.Running ? "VolumeBridge：未运行" : ok ? $"VolumeBridge：{(s.Mute ? "静音" : s.VolumeText)}" : "VolumeBridge：等待输出设备";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        if (_form.Visible) _form.ShowStatus(s);
        if (DateTime.UtcNow - _lastLog >= LogEvery)
        {
            _lastLog = DateTime.UtcNow;
            _log.Write(Engine.StatusLine(s));
        }
    }

    public void ShowWindow()
    {
        if (Quitting) return;
        _form.ShowStatus(Engine.GetStatus());
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    public void NotifyHidden()
    {
        if (_hintShown) return;
        _hintShown = true;
        _tray.ShowBalloonTip(3000, "VolumeBridge", "仍在托盘中运行。右键托盘图标可退出。", ToolTipIcon.Info);
    }

    void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        _log.Write("Windows session ending.");
        Quit();
    }

    public void Quit()
    {
        if (Quitting) return;
        Quitting = true;
        _timer.Stop();
        Engine.Stop();
        _tray.Visible = false;
        _form.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionEnding -= OnSessionEnding;
            _showWait.Unregister(null);
            _tray.Dispose();
            _timer.Dispose();
            _form.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>"检查更新": GitHub latest release → download + SHA-256 check → run the installer, then exit.</summary>
    public async Task CheckForUpdatesAsync(IWin32Window owner, Button button)
    {
        button.Enabled = false;
        string original = button.Text;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("VolumeBridge-Updater");
            button.Text = "正在检查…";
            var latest = await UpdateCheck.GetLatestAsync(http, CancellationToken.None);
            if (latest is null || !UpdateCheck.IsNewer(latest.Version, CurrentVersion))
            {
                MessageBox.Show(owner, $"已是最新版本（{CurrentVersion.ToString(3)}）。", "VolumeBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(owner, $"发现新版本 {latest.Tag}（当前 {CurrentVersion.ToString(3)}）。\n\n现在下载并安装吗？安装时需要管理员确认，完成后会自动重新打开。",
                    "VolumeBridge 更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            var progress = new Progress<double>(p => button.Text = $"正在下载 {p:P0}");
            string setup = await UpdateCheck.DownloadVerifiedAsync(http, latest,
                Path.Combine(Path.GetTempPath(), "VolumeBridgeUpdate"), progress, CancellationToken.None);
            _log.Write($"update {latest.Tag} downloaded and verified: {setup}");

            // Start the installer first (the UAC prompt may be declined), then quit so it can replace us.
            // The installer waits for this process to exit and relaunches VolumeBridge afterwards.
            try
            {
                Process.Start(new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC declined
            {
                MessageBox.Show(owner, "已取消更新。", "VolumeBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Quit();
        }
        catch (Exception e)
        {
            _log.Write($"update failed: {e}");
            MessageBox.Show(owner, $"检查或下载更新失败：{e.Message}", "VolumeBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!Quitting)
            {
                button.Text = original;
                button.Enabled = true;
            }
        }
    }
}
