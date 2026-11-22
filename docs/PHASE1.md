# 阶段 1：验证步骤

在 Windows 上做。完成后把 `probe.log`（在 exe 旁边）和下表结果交回，再进入阶段 2。

## 准备

1. 关闭 Voicemeeter（包括托盘），避免干扰。
2. Flip 6 进入 USB 音频模式并接好，在“声音设置”里确认它出现（名字可能是 **Zgmicro AUDIO**）。
3. 从 GitHub Actions 的最新一次 `build` 运行中下载 `VolumeBridge-win-x64` 工件，得到 `VolumeBridge.exe`。放到一个单独的文件夹里。

## A. Virtual Cables（首选）

1. 从 https://github.com/tarekwasfy01/Virtual-Cables/releases 安装 `Virtual_Cables_Setup.exe`。启动后点 **Download and Install Driver**（会安装 usbip-win2），然后 **Add Cable** 添加 1 条。
2. 记录：安装时 Windows 有没有要求关闭安全启动或开启测试签名？（如果要求，**停止**，直接转到 B。）
3. 在声音设置里确认出现 `Virtual Cable 01` 的播放端和录制端各一个。
4. 把 Windows 默认播放设备设为 `Virtual Cable 01`。
5. 在 exe 所在文件夹打开终端，运行 `.\VolumeBridge.exe --probe`。
6. 播放一段**音量稳定**的音频（比如粉红噪声或持续的音乐），然后：
   - 滑块依次拉到 100% → 50% → 10% → 1%，每档停 3 秒；
   - 按静音键，再取消静音；
   - 观察 `VIRT vol=`、`mute=` 和 `REC rms=` 怎么变化。
7. 按 Ctrl+C 停止。把 `probe.log` 改名为 `probe-virtualcables.log` 保存。

## B. VB-CABLE（兜底，A 不可用时做）

1. 从 https://vb-audio.com/Cable/ 安装，然后重启。
2. 默认播放设备设为 `CABLE Input`。
3. 重复 A.5–A.7，日志保存为 `probe-vbcable.log`。

## 结果表

| 问题 | Virtual Cables | VB-CABLE |
| --- | --- | --- |
| 开着安全启动能否安装（是否要求测试签名） | | |
| 是否出现一对播放和录制设备 | | |
| 滑块移动时 `VIRT vol=` 是否跟着变 | | |
| 静音时 `mute=` 是否变成 1 | | |
| 清单中播放端的 `hw=` 显示什么（`vol` 还是 `-`） | | |
| 滑块 100%→10% 时 `REC rms` 下降了多少 dB？和 `VIRT` 行括号里的 dB 变化（通常 20–40 dB）大致相同，说明 Windows 已经衰减过，应选 passthrough；基本不变则应选 apply | | |
| 静音时 `REC rms` 是否变成 `-inf` | | |
| Flip 6 的名称和格式（`OUT` 行、清单） | | |

如果 `Output: NOT SELECTED`，把清单中 Flip 6 那一行的名称发给我。也可以在 `config.json` 里设置 `outputDeviceId` 直接指定。
