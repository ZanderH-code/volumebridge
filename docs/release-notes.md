**中文** — 下载下面的 `VolumeBridgeSetup.exe` 运行即可。安装前请退出 Voicemeeter。使用说明见 [README](https://github.com/ZanderH-code/volumebridge/blob/main/README.zh-CN.md)。

**English** — Download and run `VolumeBridgeSetup.exe` below. Quit Voicemeeter first. See the [README](https://github.com/ZanderH-code/volumebridge/blob/main/README.md).

`VolumeBridgeSetup.exe.sha256` is used by the in-app updater to verify the download.

### 0.3.3
- 修复零延迟插件从未生效的根本原因：Windows 音频引擎以 COM 聚合方式创建插件，旧版本拒绝了聚合，插件实际从未被创建（与 Equalizer APO 的实现对比后发现）。 / Root cause fix: the audio engine creates APOs aggregated; earlier versions refused aggregation, so the plugin was never created (found by comparing with Equalizer APO).
- 音频进程只能读共享文件时，插件改为只读方式获取音量和上限。 / If audiodg may only read the shared file, the plugin still follows the app's volume and ceiling.
- 升级时先恢复音频服务再设置插件；设置日志改为追加。 / Upgrade starts Windows Audio before plugin setup; setup log appends.

### 0.3.2
- 新增“最大音量”设置，默认 -12 dB（滑块 100% 时的实际输出），两种模式都生效。 / New "Maximum volume" setting, default -12 dB at 100 % slider, both modes.
- 修复零延迟插件不生效：音箱上的旧版音效位（LFX/GFX，例如 Microsoft Audio Home Theater Effects）会让 Windows 忽略新版位置上的插件；现在启用插件时会先移除它们（已备份，卸载时恢复），并重启音频端点服务。 / Fix: legacy LFX/GFX effects on the speaker made Windows ignore the plugin; they are now removed while the plugin is enabled (backed up, restored on uninstall).
- 插件诊断日志 C:\ProgramData\VolumeBridge\apo-log.txt；遇到未知的初始化结构不再拒绝工作。 / Plugin diagnostic log; unknown initialization structures no longer rejected.

### 0.3.1
- 修复：安装时和“启用插件”按钮无法完成插件设置（无控制台时设置编码出错，且没有留下日志）。 / Fix: enabling the plugin from the installer or the window failed silently (console encoding set without a console).

### 0.3.0
- 新的默认模式“零延迟”：在音箱自身的音效链里加入一个音量插件（APO），按 Windows 音量滑块和静音调节，**没有附加延迟**，也不再需要虚拟音箱。 / New default "zero latency" mode: a volume plugin (APO) in the speaker's own effect chain follows the Windows slider and mute with **no added latency** and no virtual speaker.
- 代价：关闭 Windows“受保护音频路径”（少数 DRM 播放可能受影响）；需保持该音箱“音频增强”开启；卸载时全部恢复。 / Trade-offs: Windows' protected audio path is turned off (a few DRM players may be affected); "Audio enhancements" must stay on; everything is restored on uninstall.
- 原来的虚拟音箱方式保留为“兼容”模式，可在窗口里切换；它需要的 usbip-win2 驱动改为安装时可选。 / The virtual-speaker approach remains as "compatible" mode (switchable in the window); its usbip-win2 driver is now optional at install.
- 仅在 JBL Flip 6（USB 音频模式）、Windows 11 上测试 / Tested with JBL Flip 6 (USB audio mode) on Windows 11 only
