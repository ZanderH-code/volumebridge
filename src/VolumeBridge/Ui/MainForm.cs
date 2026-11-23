using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using NAudio.CoreAudioApi;
using VolumeBridge.Audio;
using VolumeBridge.Core;
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
    readonly Label _virtualCaption = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    readonly Label _virtual = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    readonly Button _makeDefault = new() { AutoSize = true };
    readonly Label _outputCaption = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    readonly ComboBox _outputs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    readonly Button _enableApo = new() { AutoSize = true };
    readonly Label _capCaption = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    readonly NumericUpDown _cap = new() { Minimum = -30, Maximum = 0, Increment = 1, Width = 60 };
    readonly Label _capUnit = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
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
    string? _speakerId;
    string? _speakerName;
    bool _speakerIsDefault = true;
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
        root.Controls.Add(Spacer());
        root.Controls.Add(Row(_virtualCaption, _virtual, _makeDefault, _enableApo));
        root.Controls.Add(Row(_outputCaption, _outputs));
        root.Controls.Add(Row(_capCaption, _cap, _capUnit));
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

        _cap.Value = (decimal)Math.Max(-30, Math.Min(0, Math.Round(_app.Config.MaxOutputDb)));
        _cap.ValueChanged += (_, _) => _app.SetMaxOutputDb((double)_cap.Value);
        _enableApo.Click += async (_, _) => await EnableApo();
        _enableApo.Visible = false;
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
        _capCaption.Text = T("最大音量（滑块 100% 时）：", "Maximum volume (slider at 100%):");
        _capUnit.Text = T("dB（0 = 音箱原始最大音量）", "dB (0 = the speaker's own full level)");
        _enableApo.Text = T("启用插件", "Enable plugin");
        _volumeCaption.Text = T("Windows 音量", "Windows volume");
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

    async Task EnableApo()
    {
        if (_speakerId is null) return;
        if (MessageBox.Show(this,
                T("将为该音箱启用音量插件：\n\n• 允许 Windows 加载未签名的音效插件（关闭“受保护音频路径”，少数 DRM 播放可能受影响）\n• 修改该音箱的音效设置（会先备份，卸载时恢复）\n• 重新启动 Windows 音频服务（声音中断约 2 秒）\n\n需要管理员确认。继续吗？",
                  "This enables the volume plugin for the speaker:\n\n• lets Windows load unsigned effect plugins (turns off the protected audio path; a few DRM players may be affected)\n• changes the speaker's effect settings (backed up first, restored on uninstall)\n• restarts the Windows Audio service (sound stops for ~2 s)\n\nNeeds administrator approval. Continue?"),
                "VolumeBridge", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            return;
        _enableApo.Enabled = false;
        bool ok = await _app.EnableApoAsync(_speakerId);
        _enableApo.Enabled = true;
        if (!ok)
            MessageBox.Show(this, T("启用插件失败或已取消。详情见 C:\\ProgramData\\VolumeBridge\\apo-setup.log。",
                                    "Enabling the plugin failed or was cancelled. See C:\\ProgramData\\VolumeBridge\\apo-setup.log."),
                "VolumeBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>Zero-latency mode status.</summary>
    public void ShowApo(ApoStatus a)
    {
        _speakerId = a.SpeakerId;
        _speakerName = a.SpeakerName;
        _virtualCaption.Text = T("音量插件：", "Volume plugin:");
        (string head, Color color, string detail) = a.State switch
        {
            ApoState.NoSpeaker => (T("● 未找到音箱", "● Speaker not found"), Color.DarkOrange,
                T("请连接音箱，或在下方“输出设备”中选择它。", "Connect the speaker, or pick it under \"Output device\".")),
            ApoState.NotInstalled => (T("● 音量插件未启用", "● Volume plugin not enabled"), Color.Firebrick,
                T($"{a.SpeakerName}：点击“启用插件”（需要管理员确认）。", $"{a.SpeakerName}: click \"Enable plugin\" (needs administrator approval).")),
            ApoState.EnhancementsOff => (T("● 音频增强已关闭", "● Audio enhancements are off"), Color.Firebrick,
                T("设置 → 系统 → 声音 → 该音箱 → 音频增强，改为“设备默认效果”。", "Settings → System → Sound → the speaker → Audio enhancements: set to \"Device Default Effects\".")),
            ApoState.Idle => (T("● 已就绪（零延迟）", "● Ready (zero latency)"), Color.ForestGreen,
                T($"{a.SpeakerName}：插件已启用，播放声音时生效。", $"{a.SpeakerName}: plugin enabled; active while audio plays.")),
            _ => (T("● 正在工作（零延迟）", "● Working (zero latency)"), Color.ForestGreen,
                T($"{a.SpeakerName}：音量由插件直接调节，无附加延迟。", $"{a.SpeakerName}: the plugin applies the volume with no added latency.")),
        };
        _headline.Text = head;
        _headline.ForeColor = color;
        _detail.Text = detail;
        _volume.Text = a.VolumeText;

        _enableApo.Visible = a.State == ApoState.NotInstalled;
        _virtual.Text = a.State switch
        {
            ApoState.NoSpeaker => "—",
            ApoState.NotInstalled => T("未启用", "not enabled"),
            _ => T("已启用", "enabled"),
        };

        if (DateTime.UtcNow - _lastDefaultCheck > TimeSpan.FromSeconds(2))
        {
            _lastDefaultCheck = DateTime.UtcNow;
            try
            {
                _speakerIsDefault = a.SpeakerId is null || DefaultDevice.IsDefault(a.SpeakerId);
                _makeDefault.Visible = !_speakerIsDefault;
                _virtualId = a.SpeakerId;
            }
            catch (Exception) { }
        }
        if (!_speakerIsDefault)
            _detail.Text += T("\n注意：该音箱不是默认输出，点击“设为默认输出”。", "\nNote: the speaker is not the default output; click \"Set as default output\".");

        var r = a.Report;
        _stats.Text = r is null ? "" :
            FormattableString.Invariant($"{T("增益", "Gain")} {(r.TargetGain <= 0.0001f ? "-∞" : (20 * Math.Log10(r.TargetGain)).ToString("0.0", CultureInfo.InvariantCulture))} dB   ") +
            FormattableString.Invariant($"{T("音量来源", "Volume source")}: {a.SourceText}\n") +
            FormattableString.Invariant($"{T("插件实例", "Plugin instances")} {r.Instances}   {r.SampleRate} Hz  {r.Channels} ch   {T("附加延迟", "Added latency")} 0 ms");
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
                T($"无法自动设置默认输出：{e.Message}\n\n请在“设置 → 系统 → 声音”中手动选择 {DeviceForDefault}。",
                  $"Could not set the default output: {e.Message}\n\nPlease choose {DeviceForDefault} in Settings → System → Sound."),
                "VolumeBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    string DeviceForDefault => _speakerName ?? T("你的音箱", "your speaker");

    sealed record OutputItem(string? Id, string Text)
    {
        public override string ToString() => Text;
    }

    void LoadOutputs()
    {
        _loadingOutputs = true;
        try
        {
            var cfg = _app.Config;
            string keywords = string.Join(" / ", cfg.OutputKeywords);
            var items = new List<OutputItem> { new(null, T($"自动（名称含 {keywords}）", $"Automatic (name contains {keywords})")) };
            using (var e = new MMDeviceEnumerator())
            {
                foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList())
                {
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
        if (item.Id == _app.Config.OutputDeviceId) return;
        _app.Config.OutputDeviceId = item.Id;
        _app.SaveConfig();
        _app.ReopenOutput();
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
            if (_autostart.Checked) Autostart.Install(Application.ExecutablePath);
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
