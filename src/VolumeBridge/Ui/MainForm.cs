using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using NAudio.CoreAudioApi;
using VolumeBridge.Audio;
using VolumeBridge.Core;

namespace VolumeBridge.Ui;

/// <summary>Status window. Closing it hides to the tray; the engine keeps running.</summary>
sealed class MainForm : Form
{
    readonly TrayApp _app;
    readonly Label _headline = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold) };
    readonly Label _detail = new() { AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Color.DimGray };
    readonly Label _volume = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold) };
    readonly ProgressBar _level = new() { Width = 300, Height = 10, Maximum = 600 };
    readonly Label _virtual = new() { AutoSize = true };
    readonly Button _makeDefault = new() { Text = "设为默认输出", AutoSize = true };
    readonly ComboBox _outputs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    readonly Label _stats = new() { AutoSize = true, ForeColor = Color.DimGray, Font = new Font("Consolas", 9f) };
    readonly CheckBox _autostart = new() { Text = "开机自动启动（在托盘中运行）", AutoSize = true };
    readonly Button _update = new() { Text = "检查更新", AutoSize = true };
    readonly Button _logs = new() { Text = "打开日志文件夹", AutoSize = true };
    readonly Button _quit = new() { Text = "退出", AutoSize = true };
    readonly Label _version = new() { AutoSize = true, ForeColor = Color.Gray };
    bool _loadingOutputs;
    bool _settingAutostart;
    string? _virtualId;
    DateTime _lastDefaultCheck;

    public MainForm(TrayApp app, Icon icon)
    {
        _app = app;
        Text = "VolumeBridge";
        Icon = icon;
        Font = new Font("Microsoft YaHei UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(16);

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        root.Controls.Add(_headline);
        root.Controls.Add(_detail);
        root.Controls.Add(Spacer());
        root.Controls.Add(new Label { Text = "Windows 音量", AutoSize = true, ForeColor = Color.Gray });
        root.Controls.Add(_volume);
        root.Controls.Add(_level);
        root.Controls.Add(Spacer());
        root.Controls.Add(Row(new Label { Text = "虚拟音箱：", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, _virtual, _makeDefault));
        root.Controls.Add(Row(new Label { Text = "输出设备：", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, _outputs));
        root.Controls.Add(Spacer());
        root.Controls.Add(_stats);
        root.Controls.Add(Spacer());
        root.Controls.Add(_autostart);
        root.Controls.Add(Row(_update, _logs, _quit));
        root.Controls.Add(_version);
        Controls.Add(root);

        _version.Text = $"版本 {TrayApp.CurrentVersion.ToString(3)}";
        _makeDefault.Click += (_, _) => MakeDefault();
        _outputs.DropDown += (_, _) => LoadOutputs();
        _outputs.SelectedIndexChanged += (_, _) => OutputChosen();
        _autostart.Checked = SafeIsAutostart();
        _autostart.CheckedChanged += (_, _) => ToggleAutostart();
        _update.Click += async (_, _) => await _app.CheckForUpdatesAsync(this, _update);
        _logs.Click += (_, _) => Process.Start(new ProcessStartInfo(_app.DataDir) { UseShellExecute = true });
        _quit.Click += (_, _) => _app.Quit();
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
            _headline.Text = "● 未运行";
            _headline.ForeColor = Color.Firebrick;
            _detail.Text = s.StartError ?? "正在启动…";
        }
        else if (!s.OutputOpen)
        {
            _headline.Text = "● 等待输出设备";
            _headline.ForeColor = Color.DarkOrange;
            _detail.Text = s.OutputText;
        }
        else
        {
            _headline.Text = "● 正在工作";
            _headline.ForeColor = Color.ForestGreen;
            _detail.Text = $"输出到 {s.OutputText}";
        }

        _volume.Text = s.Mute ? "静音" : s.VolumeText;
        double db = double.IsNegativeInfinity(s.InPeakDb) ? -60 : Math.Clamp(s.InPeakDb, -60, 0);
        _level.Value = s.Mute ? 0 : (int)((db + 60) * 10);

        if (DateTime.UtcNow - _lastDefaultCheck > TimeSpan.FromSeconds(2))
        {
            _lastDefaultCheck = DateTime.UtcNow;
            try
            {
                _virtualId = s.UsbConnected ? DefaultDevice.FindVirtualSpeakerId() : null;
                bool isDefault = _virtualId is not null && DefaultDevice.IsDefault(_virtualId);
                _virtual.Text = _virtualId is null ? "未接入" : isDefault ? "已接入，是默认输出 ✓" : "已接入，但不是默认输出";
                _makeDefault.Visible = _virtualId is not null && !isDefault;
            }
            catch (Exception) { /* audio service busy; retry on the next tick */ }
        }

        _stats.Text = string.Create(CultureInfo.InvariantCulture,
            $"缓冲 {s.BufferMs,5:0.0} ms   设备 {s.DeviceMs,5:0.0} ms   延迟约 {s.LatencyMs,3:0} ms\n" +
            $"时钟校正 {s.DriftPpm,+5:+0;-0} ppm   断音 {s.Underruns}   溢出 {s.Overruns}");
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
            MessageBox.Show(this, $"无法自动设置默认输出：{e.Message}\n\n请在“设置 → 系统 → 声音”中手动选择 VolumeBridge Speaker。", "VolumeBridge",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            var items = new List<OutputItem> { new(null, $"自动（名称含 {string.Join(" / ", cfg.OutputKeywords)}）") };
            using (var e = new MMDeviceEnumerator())
            {
                var list = e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                foreach (var d in list)
                {
                    if (!d.FriendlyName.Contains(Core.Usb.SpeakerDescriptors.Product, StringComparison.OrdinalIgnoreCase))
                        items.Add(new OutputItem(d.ID, d.FriendlyName));
                    d.Dispose();
                }
            }
            if (cfg.OutputDeviceId is { } id && items.All(i => i.Id != id)) items.Add(new OutputItem(id, "（已选设备当前未连接）"));
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
            MessageBox.Show(this, $"设置开机启动失败：{e.Message}", "VolumeBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _settingAutostart = true;
            _autostart.Checked = SafeIsAutostart();
            _settingAutostart = false;
        }
    }
}
