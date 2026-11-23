[English](README.md) | **中文**

# VolumeBridge

**JBL Flip 6 用 USB 连电脑后音量调不了、一直最大声？VolumeBridge 让 Windows 的音量滑块和静音键重新控制它，零延迟，不需要 Voicemeeter。**

> ✅ 已在 **JBL Flip 6（USB 音频模式，Windows 中显示为"扬声器 (Zgmicro AUDIO)"）+ Windows 11** 上实测：滑块调音量、静音键、零附加延迟均正常工作。
> 理论上也适用于更老的 JBL 系列（如 Flip 5、Flip 4、Charge 系列等，只要能以 USB 音频方式连接电脑），以及其他"接 USB 后系统音量无效"的音箱 / USB 声卡，但这些尚未实测，欢迎反馈。

## 你是不是遇到了这个问题

- JBL Flip 6 用 USB-C 线连接电脑，开启 USB 音频（有线）模式后，**Windows 音量滑块完全不起作用**，声音始终是最大音量。
- 把系统音量拉到 1%、甚至按**静音**，音箱照样很响。
- 音箱自己的音量键在 USB 模式下也不起作用。
- 设备在 Windows 里显示为 **"扬声器 (Zgmicro AUDIO)"** 或 "Generic USB Audio"。
- 网上的办法是装 **Voicemeeter** 中转音量，但它占资源、有延迟、设置复杂，还会独占音箱。

原因：这种 USB 音频模式（厂商的展示 / 服务模式）下，音箱固件把音量锁在最大。它向 Windows 声称"音量由硬件控制"，Windows 于是只把音量值发给音箱，自己不调小声音，而音箱又忽略这个值。结果就是系统音量对它完全无效。

VolumeBridge 在 Windows 音频引擎里、声音送到音箱之前，按音量滑块和静音状态把声音调小，效果就像音箱本来就支持音量调节一样。

## 功能

- **零延迟模式（默认）**：音量插件直接挂在音箱自己的音效链里，原地处理，**不增加任何延迟**，看视频、玩游戏口型不偏。
- Windows **音量滑块、键盘音量键、静音键**全部可用，音量变化有 20 ms 平滑过渡，没有爆音。
- **最大音量上限**：默认 -12 dB，滑块拉满也不会震耳，可以在窗口里调。
- 窗口 + **托盘运行**，关闭窗口不退出；**开机自启**；**一键检查更新**。
- 中文 / English 界面，安装时选择，也可以随时切换。
- **兼容模式（备选）**：模拟一个 USB 虚拟音箱再转发到真音箱，不改动系统音频设置，约 40 ms 延迟。
- 卸载时自动恢复所有系统设置。
- 免费开源（MIT），不需要 Voicemeeter、VB-CABLE 或 Equalizer APO。

## 安装与使用

1. 用 USB 线把音箱接到电脑，并切换到 USB 音频模式。Flip 6 的方法是同时按住**蓝牙键和播放键约 10 秒**，以你的型号为准。如果 Voicemeeter 正在运行，**请先退出**（它会独占音箱）。
2. 从 [Releases](https://github.com/ZanderH-code/volumebridge/releases/latest) 下载 `VolumeBridgeSetup.exe` 并运行，保持"**启用零延迟音量插件**"勾选。安装时 Windows 声音会中断约 2 秒。
3. 打开 VolumeBridge，点"**设为默认输出**"，把音箱设为默认播放设备。
4. 播放声音，窗口显示"**● 正在工作（零延迟）**"即表示生效。勾选"开机自动启动"，以后就不用管了。

## 工作原理

**零延迟模式（默认）**

```
应用程序 ──▶ 你的 USB 音箱（默认输出）
               └ 音效链中的 VolumeBridge 音量插件（APO）：按 Windows 音量滑块和静音调节，原地处理，无缓冲
```

- 插件（Audio Processing Object，端点效果 EFX）运行在 Windows 音频引擎（audiodg）中，把声音乘以"滑块值 × 最大音量上限"，在 20 ms 内平滑过渡。
- 音量来源：优先使用 Windows 11 发给插件的音量通知；收不到时，由托盘程序读取滑块后，通过共享内存交给插件。
- 插件和 Flip 6 原有的"Microsoft Audio Home Theater Effects"共存，不会把它挤掉。

**兼容模式（可在窗口切换）**

```
应用程序 ──▶ "VolumeBridge Speaker"（VolumeBridge 模拟的 USB 音箱）──▶ 按 dB 调增益 ──▶ 你的 USB 音箱
```

- 通过 USB/IP 模拟一个标准 USB 音箱，需要 [usbip-win2](https://github.com/vadimgrn/usbip-win2) 驱动（安装时可选）。不改动系统音频设置，但会增加约 40–60 ms 延迟。

## 代价与注意事项

- 插件没有经过微软签名。为了让 Windows 加载它，需要关闭"受保护音频路径"（注册表 `DisableProtectedAudioDG=1`，Equalizer APO 也是这样做的）。少数 DRM 播放（例如部分 4K 流媒体的音频）可能因此受影响。
- 该音箱的"**音频增强**"必须保持"设备默认效果"（设置 → 系统 → 声音 → 该音箱）。改成"关"后插件会被跳过。
- 以**独占模式**播放的程序会绕过插件。
- 换 USB 口，或 Windows 大版本更新后，插件设置可能失效。窗口会提示，点"**启用插件**"即可。
- **万一装完没声音**：在"设置 → 应用"中卸载 VolumeBridge（会恢复原设置），或者把该音箱的"音频增强"改为"关"。

## 兼容性

| 设备 | 状态 |
| --- | --- |
| JBL Flip 6（USB 音频模式，"扬声器 (Zgmicro AUDIO)"） | ✅ 实测可用（Windows 11 26H2） |
| 更老的 JBL 系列（Flip 5 / Flip 4、Charge、Pulse、Xtreme 等） | 理论可用：前提是能以 USB 音频设备方式连接电脑。有些型号的 USB 口只能充电，那就无法使用。未实测 |
| 其他"USB 连接后系统音量无效"的音箱 / USB 声卡 / DAC | 理论可用：在窗口的"输出设备"里选中它即可。未实测 |

- 系统要求：Windows 10 1903 及以上 / Windows 11，64 位。安装时需要管理员权限，平时以普通权限运行。
- 默认自动选择名称含 `JBL` 或 `Zgmicro` 的播放设备，也可以在窗口里手动指定。
- 欢迎在 [Issues](https://github.com/ZanderH-code/volumebridge/issues) 反馈你的型号能不能用。

## 常见问题

**和 Voicemeeter 比有什么好处？**
零延迟模式不经过任何中转缓冲（Voicemeeter 需要虚拟声卡加二次输出）；只做音量这一件事，后台占用极小；不独占音箱；装完基本不用设置。

**和 Equalizer APO 有什么区别？**
原理类似（都是 APO 插件），但 Equalizer APO 的音量（Preamp）是写在配置文件里的固定值，**不跟随 Windows 音量滑块**。VolumeBridge 的插件跟随滑块和静音键。

**需要一直开着 VolumeBridge 吗？**
在能收到 Windows 音量通知的系统上，插件可以独立工作。为了保险起见，建议勾选开机自启，托盘程序几乎不占资源。

## 配置

配置和日志在 `%LOCALAPPDATA%\VolumeBridge`（窗口里点"打开日志文件夹"）；插件的日志在 `C:\ProgramData\VolumeBridge`。`config.json` 中可调整：

| 项 | 默认 | 说明 |
| --- | --- | --- |
| `mode` | `apo` | `apo` 零延迟模式；`virtual` 兼容模式 |
| `maxOutputDb` | -12 | 滑块 100% 时的输出（dB），0 表示音箱原始最大音量 |
| `outputKeywords` | `["JBL","Zgmicro"]` | 自动选择设备时用的名称关键字 |
| `outputDeviceId` | `null` | 指定输出设备（界面下拉框会写这里） |
| `language` | `auto` | `zh` / `en`；`auto` 表示跟随安装时的选择 |
| `targetBufferMs` / `outputBufferMs` | 20 / 10 | 兼容模式的缓冲设置 |
| `minDb` / `maxDb` / `volumeStepDb` | -60 / 0 / 0.25 | 兼容模式中交给 Windows 的音量范围和步长 |
| `usbipPort` | 3240 | 兼容模式的本机 USB/IP 端口 |

## 从源码构建

```
dotnet test tests/VolumeBridge.Tests
dotnet publish src/VolumeBridge -c Release -r win-x64 -o out
```

音量插件（`apo/`，C++）和安装包（Inno Setup）由 CI（`.github/workflows/build.yml`）在 Windows 上构建；推送 `v*` 标签时自动发布 Release。设计与评审记录见 [docs/PLAN.md](docs/PLAN.md)。

## 致谢与许可

- 兼容模式的 UAC1 设备与 USB/IP 服务端移植自 [Virtual Cables](https://github.com/tarekwasfy01/Virtual-Cables)（BSD-2-Clause）。
- [usbip-win2](https://github.com/vadimgrn/usbip-win2)（BSD-2-Clause）可选随安装包分发，未做修改。
- [NAudio](https://github.com/naudio/NAudio)（MIT）。
- APO 的注册方式参考了 [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) 的公开行为（未复制其 GPL 代码）。
- 第三方许可全文见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。本项目以 [MIT 许可证](LICENSE) 发布。

<sub>关键词：JBL Flip 6 USB 音量无法调节、JBL Flip 6 连接电脑音量一直最大、JBL USB 有线模式声音太大、Flip 6 USB-C 音频 Windows 音量无效、系统音量滑块不起作用、静音无效、Zgmicro AUDIO 音量、JBL 展示模式 / 服务模式、Flip 5 / Flip 4 / Charge 5 USB 音频音量、USB 音箱固定音量、USB 声卡硬件音量无效、软件音量控制、Voicemeeter 替代、Equalizer APO 跟随系统音量、APO 音量插件、Windows 11 USB 音频音量</sub>
