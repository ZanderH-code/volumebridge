using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using NAudio.CoreAudioApi;
using VolumeBridge.Audio;
using VolumeBridge.Core;
using VolumeBridge.Core.Usb;
using static VolumeBridge.Core.Loc;

namespace VolumeBridge.Ui;

/// <summary>Status window. Closing it hides to the tray; the engine keeps running.</summary>
sealed class MainForm : Form
{
    readonly TrayApp _app;
    readonly Label _headline = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold) };
    readonly Label _detail = new() { AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Color.DimGray };
    readonly Label _volumeCaption = new() { AutoSize = true, ForeColor = Color.Gray };
    readonly Label _volume = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold) };
    readonly ProgressBar _level = new() { Width = 300, Height = 10, Maximum = 600 };
    readonly Label _virtualCaption = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    readonly Label _virtual = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    readonly Button _makeDefault = new() { AutoSize = true };
    readonly Label _outputCaption = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    readonly ComboBox _outputs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    readonly Label _languageCaption = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    readonly Label _stats = new() { AutoSize = true, ForeColor = Color.DimGray, Font = new Font("Consolas", 9f) };
    readonly CheckBox _autostart = new() { AutoSize = true };
    readonly Button _update = new() { AutoSize = true };
    readonly Button _logs = new() { AutoSize = true };
    readonly Button _quit = new() { AutoSize = true };
    readonly Label _version = new() { AutoSize = true, ForeColor = Color.Gray };
    bool _loadingOutputs;
    bool _settingAutostart;
    string? _virtualId;
    DateTime _lastDefaultCheck;

    public MainForm(TrayApp app, Icon icon)
    {
        _app = app;
        SuspendLayout();
        // Sizes below are in 96-DPI pixels; Dpi auto-scaling needs the design-time DPI to scale them.
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "VolumeBridge";
        Icon = icon;
        Font = new Font("Microsoft YaHei UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(16);

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        root.Controls.Add(_headline);
        root.Controls.Add(_detail);
        root.Controls.Add(Spacer());
        root.Controls.Add(_volumeCaption);
        root.Controls.Add(_volume);
        root.Controls.Add(_level);
        root.Controls.Add(Spacer());
        root.Controls.Add(Row(_virtualCaption, _virtual, _makeDefault));
        root.Controls.Add(Row(_outputCaption, _outputs));
        root.Controls.Add(Spacer());
        root.Controls.Add(_stats);
        root.Controls.Add(Spacer());
        root.Controls.Add(_autostart);
        root.Controls.Add(Row(_languageCaption, _language));
        root.Controls.Add(Row(_update, _logs, _quit));
        root.Controls.Add(_version);
        Controls.Add(root);

        _language.Items.AddRange(["简体中文", "English"]);
        _language.SelectedIndex = Current == UiLanguage.Chinese ? 0 : 1;
        _language.SelectedIndexChanged += (_, _) => _app.SetLanguage(_language.SelectedIndex == 0 ? UiLanguage.Chinese : UiLanguage.English);

        _makeDefault.Click += (_, _) => MakeDefault();
        _outputs.DropDown += (_, _) => LoadOutputs();
        _outputs.SelectedIndexChanged += (_, _) => OutputChosen();
        _autostart.Checked = SafeIsAutostart();
        _autostart.CheckedChanged += (_, _) => ToggleAutostart();
        _update.Click += async (_, _) => await _app.CheckForUpdatesAsync(this, _update);
        _logs.Click += (_, _) => Process.Start(new ProcessStartInfo(_app.DataDir) { UseShellExecute = true });
        _quit.Click += (_, _) => _app.Quit();

        ApplyTexts();
        ResumeLayout(true);
    }

    /// <summary>(Re)sets every fixed text in the current language.</summary>
    public void ApplyTexts()
    {
        _volumeCaption.Text = T("Windows 音量", "Windows volume");
        _virtualCaption.Text = T("虚拟音箱：", "Virtual speaker:");
        _makeDefault.Text = T("设为默认输出", "Set as default output");
        _outputCaption.Text = T("输出设备：", "Output device:");
        _languageCaption.Text = "语言 / Language:";
        _autostart.Text = T("开机自动启动（在托盘中运行）", "Start at logon (in the tray)");
        _update.Text = T("检查更新", "Check for updates");
        _logs.Text = T("打开日志文件夹", "Open log folder");
        _quit.Text = T("退出", "Quit");
        _version.Text = T("版本 ", "Version ") + TrayApp.CurrentVersion.ToString(3);
        _lastDefaultCheck = DateTime.MinValue;
        LoadOutputs();
    }

    static Control Spacer() => new Panel { Height = 8, Width = 1 };

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        p.Controls.AddRange(controls);
        return p;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !_app.Quitting)
        {
            e.Cancel = true;
            Hide();
            _app.NotifyHidden();
            return;
        }
        base.OnFormClosing(e);
    }

    public void ShowStatus(EngineStatus s)
    {
        if (!s.Running)
        {
            _headline.Text = T("● 未运行", "● Not running");
            _headline.ForeColor = Color.Firebrick;
            _detail.Text = s.StartError ?? T("正在启动…", "Starting…");
        }
        else if (!s.OutputOpen)
        {
            _headline.Text = T("● 等待输出设备", "● Waiting for the output device");
            _headline.ForeColor = Color.DarkOrange;
            _detail.Text = s.OutputText;
        }
        else
        {
            _headline.Text = T("● 正在工作", "● Working");
            _headline.ForeColor = Color.ForestGreen;
            _detail.Text = T("输出到 ", "Playing on ") + s.OutputText;
        }

        _volume.Text = s.Mute ? T("静音", "Muted") : s.VolumeText;
        double db = double.IsNegativeInfinity(s.InPeakDb) ? -60 : Math.Clamp(s.InPeakDb, -60, 0);
        _level.Value = s.Mute ? 0 : (int)((db + 60) * 10);

        if (DateTime.UtcNow - _lastDefaultCheck > TimeSpan.FromSeconds(2))
        {
            _lastDefaultCheck = DateTime.UtcNow;
            try
            {
                _virtualId = s.UsbConnected ? DefaultDevice.FindVirtualSpeakerId() : null;
                bool isDefault = _virtualId is not null && DefaultDevice.IsDefault(_virtualId);
                _virtual.Text = _virtualId is null ? T("未接入", "not attached")
                    : isDefault ? T("已接入，是默认输出 ✓", "attached, default output ✓")
                    : T("已接入，但不是默认输出", "attached, but not the default output");
                _makeDefault.Visible = _virtualId is not null && !isDefault;
            }
            catch (Exception) { /* audio service busy; retry on the next tick */ }
        }

        _stats.Text = string.Create(CultureInfo.InvariantCulture,
            $"{T("缓冲", "Buffer")} {s.BufferMs,5:0.0} ms   {T("设备", "Device")} {s.DeviceMs,5:0.0} ms   {T("延迟约", "Latency ~")} {s.LatencyMs,3:0} ms\n" +
            $"{T("时钟校正", "Clock trim")} {s.DriftPpm,5:+0;-0} ppm   {T("断音", "Underruns")} {s.Underruns}   {T("溢出", "Overruns")} {s.Overruns}");
    }

    void MakeDefault()
    {
        if (_virtualId is null) return;
        try
        {
            DefaultDevice.SetDefault(_virtualId);
            _lastDefaultCheck = DateTime.MinValue;
        }
        catch (Exception e)
        {
            MessageBox.Show(this,
                T($"无法自动设置默认输出：{e.Message}\n\n请在“设置 → 系统 → 声音”中手动选择 VolumeBridge Speaker。",
                  $"Could not set the default output: {e.Message}\n\nPlease choose VolumeBridge Speaker in Settings → System → Sound."),
                "VolumeBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    sealed record OutputItem(string? Id, string Text)
    {
        public override string ToString() => Text;
    }

    void LoadOutputs()
    {
        _loadingOutputs = true;
        try
        {
            var cfg = _app.Engine.Config;
            string keywords = string.Join(" / ", cfg.OutputKeywords);
            var items = new List<OutputItem> { new(null, T($"自动（名称含 {keywords}）", $"Automatic (name contains {keywords})")) };
            using (var e = new MMDeviceEnumerator())
            {
                foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList())
                {
                    if (!d.FriendlyName.Contains(SpeakerDescriptors.Product, StringComparison.OrdinalIgnoreCase))
                        items.Add(new OutputItem(d.ID, d.FriendlyName));
                    d.Dispose();
                }
            }
            if (cfg.OutputDeviceId is { } id && items.All(i => i.Id != id))
                items.Add(new OutputItem(id, T("（已选设备当前未连接）", "(selected device not connected)")));
            _outputs.BeginUpdate();
            _outputs.Items.Clear();
            foreach (var i in items) _outputs.Items.Add(i);
            _outputs.SelectedItem = items.FirstOrDefault(i => i.Id == cfg.OutputDeviceId) ?? items[0];
            _outputs.EndUpdate();
        }
        catch (Exception) { /* keep the previous list */ }
        finally { _loadingOutputs = false; }
    }

    void OutputChosen()
    {
        if (_loadingOutputs || _outputs.SelectedItem is not OutputItem item) return;
        if (item.Id == _app.Engine.Config.OutputDeviceId) return;
        _app.Engine.Config.OutputDeviceId = item.Id;
        _app.SaveConfig();
        _app.Engine.ReopenOutput();
    }

    static bool SafeIsAutostart()
    {
        try { return Autostart.IsInstalled(); } catch (Exception) { return false; }
    }

    void ToggleAutostart()
    {
        if (_settingAutostart) return;
        try
        {
            if (_autostart.Checked) Autostart.Install(Environment.ProcessPath!);
            else Autostart.Uninstall();
        }
        catch (Exception e)
        {
            MessageBox.Show(this, T($"设置开机启动失败：{e.Message}", $"Could not change start at logon: {e.Message}"),
                "VolumeBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _settingAutostart = true;
            _autostart.Checked = SafeIsAutostart();
            _settingAutostart = false;
        }
    }
}
