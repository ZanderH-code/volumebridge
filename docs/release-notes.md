**中文** — 下载下面的 `VolumeBridgeSetup.exe` 运行即可。安装前请退出 Voicemeeter。使用说明见 [README](https://github.com/ZanderH-code/volumebridge/blob/main/README.zh-CN.md)。

**English** — Download and run `VolumeBridgeSetup.exe` below. Quit Voicemeeter first. See the [README](https://github.com/ZanderH-code/volumebridge/blob/main/README.md).

`VolumeBridgeSetup.exe.sha256` is used by the in-app updater to verify the download.

### 0.3.0
- 新的默认模式“零延迟”：在音箱自身的音效链里加入一个音量插件（APO），按 Windows 音量滑块和静音调节，**没有附加延迟**，也不再需要虚拟音箱。 / New default "zero latency" mode: a volume plugin (APO) in the speaker's own effect chain follows the Windows slider and mute with **no added latency** and no virtual speaker.
- 代价：关闭 Windows“受保护音频路径”（少数 DRM 播放可能受影响）；需保持该音箱“音频增强”开启；卸载时全部恢复。 / Trade-offs: Windows' protected audio path is turned off (a few DRM players may be affected); "Audio enhancements" must stay on; everything is restored on uninstall.
- 原来的虚拟音箱方式保留为“兼容”模式，可在窗口里切换；它需要的 usbip-win2 驱动改为安装时可选。 / The virtual-speaker approach remains as "compatible" mode (switchable in the window); its usbip-win2 driver is now optional at install.
- 仅在 JBL Flip 6（USB 音频模式）、Windows 11 上测试 / Tested with JBL Flip 6 (USB audio mode) on Windows 11 only
