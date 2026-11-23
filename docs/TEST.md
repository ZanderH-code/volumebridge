# 测试：用 VolumeBridge 代替 Voicemeeter

装好后，声音从 **VolumeBridge Speaker** 送到 Flip 6，Windows 音量滑块和静音键直接控制 Flip 6 的音量。

## 1. 安装

0. 退出 Voicemeeter（包括托盘图标）。之前手动装过 usbip-win2 的，不用卸载。
1. 打开 https://github.com/ZanderH-code/volumebridge/actions ，进入最新一次 `build`，在页面底部 Artifacts 里下载 **VolumeBridgeSetup**，解压后运行 `VolumeBridgeSetup.exe`。
2. 安装包会自动安装 usbip-win2 驱动。装驱动时 USB 设备会断开几秒，属于正常。
   - 如果弹出"usbip-win2 未能安装"，截图发给我。
   - 结束页如果提示重启，**先选"稍后重启"**，直接做第 2 步。不重启用不了的话，再重启。

## 2. 使用

1. Flip 6 保持 USB 音频模式并接好，**退出 Voicemeeter**。
2. 开始菜单 → **VolumeBridge**。窗口顶部显示"● 正在工作"和"输出到 扬声器 (Zgmicro AUDIO)（48000 Hz）"。
3. 点 **设为默认输出**。成功后，"虚拟音箱"一行显示"已接入，是默认输出 ✓"。
4. 播放音乐，Flip 6 出声；窗口里的音量数值和电平条跟着变化。
5. 关闭窗口，程序继续在托盘运行；双击托盘图标重新打开窗口，右键托盘图标 → 退出。
6. 勾选"开机自动启动"，注销后重新登录（或重启），确认 VolumeBridge 自动出现在托盘里，并能正常出声。

## 3. 验收

| 项目 | 怎么测 | 结果 |
| --- | --- | --- |
| 音量 | 滑块从 100% 拉到 1%，响度应连续、明显下降，窗口里的 dB 值跟着变 | |
| 静音 | 按静音键完全无声，取消静音后恢复 | |
| 爆音 | 调音量时没有咔哒声；连续播放 1 小时没有爆音、断音 | |
| 延迟 | 窗口里的"延迟约"（VolumeBridge 自身的缓冲；另有 Windows 和 USB 约 10–20 ms 未计入）；看视频时口型是否对得上 | |
| 漂移 | 连续播放 1 小时，"缓冲"一直在 20–30 ms，"时钟校正"稳定在一个小数值（通常几十 ppm），"断音""溢出"在播放期间不增长（暂停或停止播放会让"断音"加 1，属于正常） | |
| 拔插 | 播放中拔掉 Flip 6 再插回，几秒内自动恢复出声 | |
| 重启程序 | 关掉再打开 VolumeBridge，Windows 仍记得 VolumeBridge Speaker 是默认输出，名字没变成 "2- ..." | |
| 托盘 / 自启 / 更新 | 见第 2 步；"检查更新"在没有更新版本时应提示"已是最新版本" | |
| 语言 | 安装时可选 English / 简体中文，程序界面跟随；窗口里"语言 / Language"切换后立即生效 | |

如果找不到 Flip 6，窗口会显示"等待输出设备"。在"输出设备"下拉框里手动选它即可。

## 交回

- 窗口里点 **打开日志文件夹** → `volumebridge.log`（每次启动会重新写，超过 2 MB 时旧内容转存为 `volumebridge.1.log`）。
- 上面的结果表。

## 恢复 Voicemeeter

把默认输出切回 Voicemeeter Input，再打开 Voicemeeter。卸载时，用开始菜单里的"卸载 VolumeBridge"，它会询问是否同时卸载 usbip-win2。
