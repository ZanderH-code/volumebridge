# 测试：用 VolumeBridge 代替 Voicemeeter

装好后，声音从 **VolumeBridge Speaker** 送到 Flip 6，Windows 音量滑块和静音键直接控制 Flip 6 的音量。

## 1. 安装

0. 退出 Voicemeeter（包括托盘图标）。之前手动装过 usbip-win2 的，不用卸载。
1. 打开 https://github.com/ZanderH-code/volumebridge/actions ，进入最新一次 `build`，在页面底部 Artifacts 里下载 **VolumeBridgeSetup**，解压后运行 `VolumeBridgeSetup.exe`。
2. 安装包会自动安装 usbip-win2 驱动。装驱动时 USB 设备会断开几秒，属于正常。
   - 如果弹出"usbip-win2 未能安装"，截图发给我。
   - 结束页如果提示重启，**先选"稍后重启"**，直接做第 2 步。不重启用不了的话，再重启。

## 2. 使用

1. Flip 6 保持 USB 音频模式并接好。
2. 开始菜单 → VolumeBridge → **VolumeBridge**。会打开一个控制台窗口（以后会改成托盘图标）。
3. 日志里依次出现：
   - `Attached as usbip port N`
   - `output opened: "..." 48000Hz ...`：这里会显示 Flip 6 的名称和格式。
4. 在"设置 → 系统 → 声音"里把 **VolumeBridge Speaker** 设为默认输出。
5. 播放音乐，Flip 6 应该出声。

退出：在控制台窗口里按 Ctrl+C，或直接关闭窗口。

## 3. 验收

| 项目 | 怎么测 | 结果 |
| --- | --- | --- |
| 音量 | 滑块从 100% 拉到 1%，响度应连续、明显下降，日志出现 `VOLUME from Windows: ... dB` | |
| 静音 | 按静音键完全无声，取消静音后恢复 | |
| 爆音 | 调音量时没有咔哒声；连续播放 1 小时没有爆音、断音 | |
| 延迟 | 看日志 `latency~`（VolumeBridge 自身的缓冲；另有 Windows 和 USB 约 10–20 ms 未计入）；看视频时口型是否对得上 | |
| 漂移 | 连续播放 1 小时，`buffer=` 一直在 20–30 ms，`drift=` 稳定在一个小数值（通常几十 ppm），`underruns`/`overruns` 在播放期间不增长（暂停或停止播放会让 `underruns` 加 1，属于正常） | |
| 拔插 | 播放中拔掉 Flip 6 再插回，几秒内自动恢复出声 | |
| 重启程序 | 关掉再打开 VolumeBridge，Windows 仍记得 VolumeBridge Speaker 是默认输出，名字没变成 "2- ..." | |

如果找不到 Flip 6，日志会显示 `output not available: ...`。把开始菜单 → **VolumeBridge 设备诊断** 列出的设备名发给我。

## 交回

- 开始菜单 → **打开日志文件夹** → `volumebridge.log`。每次运行都会覆盖，有问题时请先改名保存。
- 上面的结果表。

## 恢复 Voicemeeter

把默认输出切回 Voicemeeter Input，再打开 Voicemeeter。卸载时，用开始菜单里的"卸载 VolumeBridge"，它会询问是否同时卸载 usbip-win2。
