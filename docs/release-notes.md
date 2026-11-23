**中文** — 下载下面的 `VolumeBridgeSetup.exe` 运行即可（会自动安装 usbip-win2 驱动）。安装前请退出 Voicemeeter。使用说明见 [README](https://github.com/ZanderH-code/volumebridge/blob/main/README.zh-CN.md)。

**English** — Download and run `VolumeBridgeSetup.exe` below (it installs the usbip-win2 driver). Quit Voicemeeter first. See the [README](https://github.com/ZanderH-code/volumebridge/blob/main/README.md).

`VolumeBridgeSetup.exe.sha256` is used by the in-app updater to verify the download.

### 0.2.0
- 窗口界面 + 托盘运行，没有控制台窗口 / Window + tray, no console window
- 一键设为默认输出、输出设备选择、开机自启、一键更新 / Set-as-default button, output picker, start at logon, one-click update
- 音质：48 kHz 原样传递，时钟漂移靠调节虚拟时钟吸收；延迟默认约 20 ms 缓冲 / 48 kHz bit-exact path (gain only), drift absorbed by clock steering; ~20 ms default buffer
- 仅在 JBL Flip 6（USB 音频模式）上实测 / Tested with JBL Flip 6 (USB audio mode) only
