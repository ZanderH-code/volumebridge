[English](README.md) | **中文**

# VolumeBridge

**JBL Flip 6 用 USB 连接电脑后音量无法调节、一直最大声？** VolumeBridge 让 Windows 的音量滑块、键盘音量键和静音键重新控制它——零附加延迟，不需要 Voicemeeter。

> ✅ **实测环境：** JBL Flip 6（USB 音频有线模式，Windows 中显示为 `扬声器 (Zgmicro AUDIO)`）+ Windows 11（26H2）。
> 更老的 JBL 型号（Flip 5、Flip 4、Charge、Pulse、Xtreme 等）以及其他接 USB 后忽略 Windows 音量的音箱或 USB 声卡/DAC，只要能作为 USB 音频设备连上电脑，理论上同样可用，但尚未实测。欢迎在 [Issues](https://github.com/ZanderH-code/volumebridge/issues) 反馈你的设备情况。

## 为什么会这样：USB 音箱锁死最大音量

如果你遇到的是下面这种情况，VolumeBridge 就是为此做的：

- JBL Flip 6 通过 USB 线连到 Windows 电脑并进入 USB 音频（有线）模式后，**声音一直锁在最大音量**。
- 拖动 Windows 音量滑块、按键盘音量键甚至点**静音**都没有用，调到 1% 依然很响。
- 音箱机身自己的音量加减键在 USB 模式下也不起作用。
- Windows 里这个设备显示为**“扬声器 (Zgmicro AUDIO)”**或 **Generic USB Audio**。
- 网上常见的办法是装 **Voicemeeter** 中转音量，但它会增加延迟、独占音箱，只为调个音量却要折腾一堆设置。

**原因：** JBL 的 USB 有线音频模式属于厂商展示/服务模式，固件把音量锁死在最大。同时它向 Windows 汇报“由硬件控制音量”，导致 Windows 只把音量值发给音箱、自己不做衰减，而音箱又忽略这个值，结果系统音量完全失效。

VolumeBridge 在 Windows 音频引擎内部、声音发往音箱之前，按系统音量滑块和静音状态做衰减，并带有 20 ms 平滑过渡，避免爆音。

## 安装与使用

**系统要求：** Windows 10（1903 及以上）或 Windows 11，64 位（x64）。绿色单文件，无需安装。启用或移除插件时需要一次管理员（UAC）确认，平时以普通权限运行。

> **从 0.4.0 及更早版本升级：** 请先在 **设置 → 应用** 中卸载旧的安装版，再运行新版 `VolumeBridge.exe`。

1. **把音箱切到 USB 音频模式**并连上电脑。JBL Flip 6 的操作是同时按住**蓝牙键 + 播放键约 10 秒**（其他型号请按对应方式操作）。如果正在运行 Voicemeeter，**请先退出**，避免它独占音箱。
2. **下载并运行 `VolumeBridge.exe`**（前往 [最新 Release](https://github.com/ZanderH-code/volumebridge/releases/latest)，放在任意文件夹即可）。由于程序未做代码签名，Windows SmartScreen 可能会弹出提示，点击**更多信息 → 仍要运行**即可。
3. **点击启用插件**（一次 UAC 管理员确认）。过程中 Windows 声音会中断约 2 秒。
4. **点击设为默认输出**，播放任意声音确认窗口显示 **● 正在工作（零延迟）**，再勾选**开机自动启动**即可。

### 日常使用

- **最大音量上限：** 默认 **-12 dB**，避免滑块拉满时震耳欲聋；可在窗口中自由调节（**0 到 -30 dB**）。
- **输出设备选择：** 默认自动匹配名称包含 `JBL` 或 `Zgmicro` 的播放设备，也可以在下拉框里手动指定其他设备。
- **窗口与托盘：** 关闭窗口后会隐藏到系统托盘继续运行，不占任务栏。
- **界面语言：** 默认跟随 Windows 显示语言，也可在程序里随时切换简体中文 / English。
- **检查更新：** 支持从 GitHub Releases 一键原地更新（下载文件会做 SHA-256 校验）。
- **干净卸载：** 在窗口中点击**移除插件**（会自动恢复所有被修改的 Windows 音频设置并取消开机自启），然后退出并删除 `VolumeBridge.exe` 即可。

## 代价与排障

零延迟模式需要改动一项 Windows 音频设置以加载插件，启用前请了解以下几点：

- **需要关闭受保护音频路径（`DisableProtectedAudioDG=1`）：** 插件未经微软签名，Windows 需要关闭此项才能加载它（Equalizer APO 也是同样做法）。少数 DRM 播放器（如部分受保护的 4K 音频流）可能受影响。
- **保持“音频增强”开启：** 在 **设置 → 系统 → 声音 → 该音箱** 中，**音频增强**必须保持为**设备默认效果**。如果改成“关”，Windows 会跳过插件。
- **独占模式会绕过插件：** 以 WASAPI 独占模式播放的程序不经过 Windows 音频引擎，音量插件不会生效。
- **换 USB 口或 Windows 大版本更新：** 换插 USB 口或升级 Windows 大版本后，插件设置可能被重置。窗口会提示状态异常，点一下**启用插件**即可恢复。
- **如果启用后没声音：** 在 VolumeBridge 中点击**移除插件**（会还原所有设置），或者把该音箱的**音频增强**改成**关**。

## 兼容性与支持设备

| 设备 | 状态 | 说明 |
| --- | --- | --- |
| JBL Flip 6（USB 音频模式，“扬声器 (Zgmicro AUDIO)”） | ✅ 已实测 | 在 Windows 11（26H2）上验证通过 |
| 更老的 JBL 音箱（Flip 5、Flip 4、Charge、Pulse、Xtreme 等） | 未实测 | 只要能作为 USB 音频设备连电脑，理论上可用；部分型号的 USB 口仅支持充电，则无法使用。 |
| 其他接 USB 后忽略系统音量的音箱 / USB 声卡 / DAC | 未实测 | 理论上可用，在窗口的**输出设备**中手动选择即可。 |

如果你在其他型号上试过，欢迎到 [Issues](https://github.com/ZanderH-code/volumebridge/issues) 留言反馈。

## 常见问题

**为什么不用 Voicemeeter？**
Voicemeeter 需要经过虚拟声卡再二次输出，会引入延迟、占用更多资源、独占音箱，配置也繁琐。VolumeBridge 的零延迟模式直接在音频引擎中原地处理，没有附加延迟，装完几乎不用折腾。

**和 Equalizer APO 有什么区别？**
两者都是通过 Windows 音频处理对象（APO）插件工作，但 Equalizer APO 的 Preamp 是配置文件里的固定增益，**不会跟随 Windows 音量滑块变化**。VolumeBridge 的插件直接跟随系统音量滑块和静音键。

**VolumeBridge 需要一直开着吗？**
在 Windows 11 能正常下发 APO 音量通知的环境下，插件可以独立工作。不过仍建议勾选**开机自动启动**：当系统没有下发通知时，托盘程序会作为兜底把滑块状态传给插件，平时资源占用极低。

## 工作原理

```text
应用程序 ──▶ 你的 USB 音箱（默认输出）
               └ VolumeBridge 音量插件（audiodg 中的 EFX APO）：按“滑块 × 最大音量上限”原地衰减（20 ms 平滑）
```

- 以未签名的端点效果音频处理对象（EFX APO）运行在 Windows 音频引擎（`audiodg`）中，原地处理，没有额外缓冲。
- 优先使用 Windows 11 的 APO 音量通知；托盘程序同时通过共享内存传递滑块状态作为兜底。
- 与音箱原有的 **Microsoft Audio Home Theater Effects** 共存。
- 卸载时会还原“受保护音频路径”开关（`DisableProtectedAudioDG`）和音箱原来的音效设置。

## 配置与日志

- **配置与应用日志：** `%LOCALAPPDATA%\VolumeBridge`（也可在窗口中点**打开日志文件夹**）。
- **插件日志：** `C:\ProgramData\VolumeBridge`。

`%LOCALAPPDATA%\VolumeBridge\config.json` 中的主要配置项：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `maxOutputDb` | `-12` | 滑块 100% 时的输出上限（dB），`0` 表示音箱原始最大音量 |
| `outputKeywords` | `["JBL", "Zgmicro"]` | 自动识别输出设备的名称关键字（不区分大小写） |
| `outputDeviceId` | `null` | 固定指定的输出设备 ID（由界面下拉框写入） |
| `language` | `"auto"` | `"zh"` / `"en"`，`"auto"` 跟随 Windows 显示语言 |

## 从源码构建

```powershell
# 1. 构建 C++ APO 插件（在 Visual Studio x64 开发者命令提示符中）
./apo/build.ps1 -Out "$PWD\out"

# 2. 运行测试并构建绿色单文件 exe（.NET Framework 4.8）
dotnet test tests/VolumeBridge.Tests
dotnet build src/VolumeBridge -c Release "-p:ApoDll=$PWD\out\VolumeBridgeApo.dll" -p:RequireApo=true -o dist
```

GitHub Actions（`.github/workflows/build.yml`）会在 Windows 上构建 C++ 音量插件（`apo/`）并嵌入单个绿色版 `VolumeBridge.exe`，并在推送 `v*` 标签时自动发布 Release。设计思路与评审记录见 [docs/PLAN.md](docs/PLAN.md)。

## 致谢与许可

- [NAudio](https://github.com/naudio/NAudio)（MIT）。
- APO 注册方式参考了 [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) 的公开行为（未复制其 GPL 代码）。
- 第三方许可全文见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。VolumeBridge 基于 [MIT 许可证](LICENSE) 开源。

<sub>关键词：JBL Flip 6 连接电脑 音量无法调节, JBL Flip 6 一直最大声, JBL Flip 6 USB 音频声音太大, Windows 11 USB 音箱音量滑块无效, 静音无效, 扬声器 Zgmicro AUDIO, Generic USB Audio 固定音量, JBL 展示模式 服务模式, Flip 5 Flip 4 Charge USB 音频音量, USB DAC 固定音量, Voicemeeter 替代, Equalizer APO 跟随系统音量滑块, Windows 11 APO 音量插件</sub>
