# 测试 0.3.0：零延迟模式

## 安装

1. 接好 Flip 6（USB 音频模式），**退出 Voicemeeter**。
2. 从 https://github.com/ZanderH-code/volumebridge/releases/latest 下载 `VolumeBridgeSetup.exe` 运行，覆盖安装 0.2.0 即可。保持 **"启用零延迟音量插件"** 勾选；"usbip-win2 驱动"不用勾选（你已经装过）。
3. 安装时 Windows 声音会中断约 2 秒，属于正常。

## 检查

1. 打开 VolumeBridge（开始菜单或托盘）。窗口顶部应显示 **"● 已就绪（零延迟）"**，播放声音时变为 **"● 正在工作（零延迟）"**。
2. 点 **设为默认输出**，把"扬声器 (Zgmicro AUDIO)"设为默认播放设备（不再是 VolumeBridge Speaker）。
3. 播放音乐，按下表测试：

| 项目 | 怎么测 | 结果 |
| --- | --- | --- |
| 音量 | 滑块 100% → 50% → 10% → 1%，响度连续明显下降 | |
| 静音 | 静音键完全无声，取消后恢复 | |
| 爆音 | 拖动滑块时没有咔哒声 | |
| 延迟 | 看视频时口型是否比 0.2.0 更准；窗口显示"附加延迟 0 ms" | |
| 音量来源 | 窗口"音量来源"显示什么（"Windows 音量通知"或"VolumeBridge 托盘程序"） | |
| 退出托盘程序 | 右键托盘退出后，拖动滑块是否仍然有效（通知方式下应仍有效） | |
| 拔插 | 拔掉 Flip 6 再插回（同一个 USB 口），恢复出声且音量正确 | |
| 换 USB 口 | 插到另一个 USB 口，窗口是否提示需要"启用插件" | |

## 万一没声音

- 在声音设置里把该音箱的"音频增强"改为"关"，插件会被跳过（音量回到固定最大）；或
- 在"设置 → 应用"中卸载 VolumeBridge，会恢复原来的设置。

## 交回

- 窗口截图（显示状态、音量来源那一块）。
- `%LOCALAPPDATA%\VolumeBridge\volumebridge.log` 和 `C:\ProgramData\VolumeBridge\apo-setup.log`。
