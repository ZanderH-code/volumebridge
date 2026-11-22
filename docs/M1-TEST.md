# M1 测试：VolumeBridge 虚拟音箱

本测试验证地基：usbip-win2 能否装上，VolumeBridge 模拟的 USB 音箱能否出现，Windows 能否把音量和静音指令发给它。
**这一版还不会把声音转到 Flip 6**，播放时听不到声音是正常的。

测试前请退出 Voicemeeter。测试结束后，把默认播放设备切回 Voicemeeter Input，再重新打开 Voicemeeter，即可恢复原状。

## 1. 安装

0. 如果之前按旧步骤手动装过 usbip-win2，不用卸载，安装包会检测到并跳过。
1. 从 GitHub Actions 最新一次 `build` 下载 `VolumeBridgeSetup` 工件，解压得到 `VolumeBridgeSetup.exe`，双击运行。安装包会自动安装 usbip-win2 0.9.8.1 驱动（已安装同版本或更新版本时跳过）。
2. 记录：
   - 是否弹出"usbip-win2 未能安装"？如果弹出，截图发给我。
   - 安装过程中 USB 设备有没有短暂断开？
   - 结束页是否提示重启？**先选"稍后重启"**，直接做第 2 步，看不重启能不能用。

## 2. 运行

1. 开始菜单 → VolumeBridge → **VolumeBridge 测试（虚拟音箱）**。会打开一个控制台窗口，日志同时写入安装目录下的 `volumebridge.log`；开始菜单里的"打开日志文件夹"可以直接打开它。
2. 期望日志依次出现：
   - `USB/IP server on 127.0.0.1:3240 ...`
   - `usbip --tcp-port 3240 attach ... -> exit 0`
   - `Attached as usbip port N`
   - 每秒一行 `session=up ...`

   如果出现 `ATTACH FAILED`：
   - 重启电脑后再试一次；
   - 还不行，右键快捷方式"以管理员身份运行"再试一次，把两次的结果都记下来。
3. 退出方式：按 Ctrl+C，或直接关闭窗口，两种都会先断开虚拟音箱再退出。

## 3. 在 Windows 里检查

1. 打开"设置 → 系统 → 声音"，看有没有出现 **VolumeBridge Speaker**。把它设为默认输出设备。
2. 播放一段音乐，看日志中的 `streaming=1`、`rate=` 约 48000fps、`level rms=` 有数值。
3. 把音量滑块依次拉到 100%、50%、10%、1%，每档停 3 秒，然后按静音、取消静音。日志应出现 `VOLUME from Windows: ... dB mute=...`。
4. 记录每档对应的 dB 值。

## 4. 稳定性

1. 让它播放 10 分钟，看 `maxLate=` 是否一直小于约 2 ms，`session` 是否一直是 `up`。
2. 按 Ctrl+C 退出，日志应出现 `Stopping: detaching ...`，VolumeBridge Speaker 从声音设置中消失。再运行一次，这次**直接关闭窗口**退出，看效果是否相同。
3. 再运行一次。在 Windows 声音设置里检查：
   - 设备名有没有变成 "2- VolumeBridge Speaker"；
   - 之前的默认输出选择是否保持。
4. （可选）让电脑睡眠，唤醒后**等约 35 秒**再看日志。M1 还没有实现唤醒后主动重连（M3 才做），要靠 usbip-win2 约 30 秒后自动重连。记录 `session` 是一直 `up`、约 30 秒后恢复，还是一直 `DOWN`。

## 交回

- `volumebridge.log`（在安装目录中，每次运行会覆盖，所以每次测试后请改名保存）。
- 下表：

| 项目 | 结果 |
| --- | --- |
| 安装包能否装好 usbip-win2 / 是否提示重启 | |
| 普通权限能否 attach（不重启 / 重启后） | |
| 是否出现 VolumeBridge Speaker | |
| 滑块 100% / 50% / 10% / 1% 对应的 dB | |
| 静音、取消静音是否收到 | |
| rate、maxLate、10 分钟内 session 是否稳定 | |
| 重启 VolumeBridge 后设备名和默认选择是否保持 | |
| 睡眠唤醒（可选） | |
