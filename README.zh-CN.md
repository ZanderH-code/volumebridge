[English](README.md) | **中文**

# VolumeBridge

**让 Windows 音量滑块和静音键控制一台"不认系统音量"的 USB 音箱，不需要 Voicemeeter。**

有些 USB 音箱或 USB 声卡在 Windows 下是固定音量输出，例如 USB 音频模式下的 JBL Flip 6：系统音量拉到 1% 和 100% 一样响。常见的解决办法是装 Voicemeeter，再让它在中间做音量转换。VolumeBridge 只做这一件事：一个安装包，一个托盘图标。

## 工作原理

**零延迟模式（默认）**

```
应用程序 ──▶ 你的 USB 音箱（默认输出）
               └ 音效链中的 VolumeBridge 音量插件（APO）：按 Windows 音量滑块和静音调节，原地处理，无缓冲
```

- 插件挂在音箱自己的音效链里（端点效果 EFX），在 Windows 音频引擎内部按滑块值乘增益，带 20 ms 平滑过渡，**不增加任何延迟**。
- 音量来源：优先用 Windows 11 给插件的音量通知；收不到时，由托盘程序读取滑块后交给插件。
- **代价**：插件未签名，需要关闭 Windows 的"受保护音频路径"（`DisableProtectedAudioDG=1`），少数 DRM 播放可能受影响。该音箱的"音频增强"必须保持开启。独占模式播放的程序会绕过插件。卸载时全部恢复。

**兼容模式（可在窗口切换）**

```
应用程序 ──▶ "VolumeBridge Speaker"（VolumeBridge 模拟的 USB 音箱）──▶ 按 dB 调增益 ──▶ 你的 USB 音箱
```

- 通过 USB/IP 模拟一个标准 USB 音箱，需要 [usbip-win2](https://github.com/vadimgrn/usbip-win2) 驱动（安装时可选）。不改动系统音频设置，但会增加约 40–60 ms 延迟。

## 兼容性

- **实测通过**：JBL Flip 6，USB 音频模式（Windows 里显示为"扬声器 (Zgmicro AUDIO)"）。
- **理论上**：任何在 Windows 里显示为播放设备、但不响应系统音量的 USB 音箱或 USB 声卡都能用，在界面的"输出设备"里选中它即可。默认会自动选择名称含 `JBL` 或 `Zgmicro` 的设备。
- 其他 JBL 型号能否使用，取决于它的 USB 口能否作为音频设备连接电脑。有些型号的 USB 口只能充电，这种无法使用。欢迎提交 Issue 反馈实测结果。
- 系统要求：Windows 10 1903 及以上，或 Windows 11，64 位。安装时需要管理员权限，平时以普通权限运行。

## 安装与使用

1. 接好音箱，**先退出 Voicemeeter**（它会独占音箱）。
2. 从 [Releases](https://github.com/ZanderH-code/volumebridge/releases/latest) 下载 `VolumeBridgeSetup.exe` 并运行，保持"启用零延迟音量插件"勾选。安装时 Windows 音频会中断约 2 秒。
3. 打开 VolumeBridge，确认显示"已就绪/正在工作（零延迟）"，再点 **"设为默认输出"** 把音箱设为默认播放设备。
4. 勾选 **"开机自动启动"**。关闭窗口后程序会继续在托盘里运行；右键托盘图标可以退出。
5. 有新版本时点 **"检查更新"**，会自动下载、校验并安装。安装时需要一次管理员确认。

安装时可以选择 English 或简体中文，程序界面跟随这个选择，之后也可以在窗口里切换。

配置和日志放在 `%LOCALAPPDATA%\VolumeBridge`（窗口里点"打开日志文件夹"即可打开）。`config.json` 中可调整：

| 项 | 默认 | 说明 |
| --- | --- | --- |
| `outputKeywords` | `["JBL","Zgmicro"]` | 自动选择输出设备时用的名称关键字 |
| `outputDeviceId` | `null` | 指定输出设备（界面下拉框会写这里） |
| `targetBufferMs` | 20 | 缓冲目标。出现断音时可调到 30–40 |
| `outputBufferMs` | 10 | 音箱端 WASAPI 缓冲 |
| `minDb` / `maxDb` / `volumeStepDb` | -60 / 0 / 0.25 | 交给 Windows 的音量范围和步长 |
| `smoothingMs` | 20 | 音量变化的平滑时长 |
| `mode` | `apo` | `apo` 零延迟模式；`virtual` 兼容模式 |
| `usbipPort` | 3240 | 本机 USB/IP 端口（兼容模式） |
| `language` | `auto` | `zh` / `en`；`auto` 表示跟随安装时的选择，没有则跟随 Windows 语言 |

## 路线图

- 以 WASAPI 独占模式输出到音箱（更低延迟，16-bit 原样送达）。
- 更小的中转缓冲，只在需要时自动加大。
- 虚拟音箱升级为 USB Audio 2.0 高速设备（1 ms 一包，用反馈端点同步时钟）。

## 已知局限

- **万一装完没声音**：在"设置 → 应用"中卸载 VolumeBridge（会恢复原设置），或在声音设置里把该音箱的"音频增强"改为"关"（插件即被跳过）。

- 兼容模式依赖第三方驱动 usbip-win2。
- Windows 大版本更新或换 USB 口后，插件设置可能失效，窗口会提示，点"启用插件"即可。
- 没有混音台、多路输出等 Voicemeeter 的功能。
- 尚未处理：通话时 Windows 自动压低其他声音（ducking）；睡眠唤醒后要等约 30 秒才自动恢复连接。
- 卸载时会询问是否一并卸载 usbip-win2（其他软件可能也在使用它），卸载驱动后需要重启。

## 从源码构建

```
dotnet test tests/VolumeBridge.Tests
dotnet publish src/VolumeBridge -c Release -r win-x64 -o out
```

在 Linux 上也可以交叉编译。安装包由 CI（`.github/workflows/build.yml`）在 Windows 上用 Inno Setup 生成；推送 `v*` 标签时自动发布 Release。设计与评审记录见 [docs/PLAN.md](docs/PLAN.md)，测试步骤见 [docs/TEST.md](docs/TEST.md)。

## 致谢与许可

- UAC1 设备描述与 USB/IP 服务端移植自 [Virtual Cables](https://github.com/tarekwasfy01/Virtual-Cables)（BSD-2-Clause）。
- [usbip-win2](https://github.com/vadimgrn/usbip-win2)（BSD-2-Clause）随安装包分发，未做修改。
- [NAudio](https://github.com/naudio/NAudio)（MIT）。
- 第三方许可全文见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。本项目以 [MIT 许可证](LICENSE) 发布。
