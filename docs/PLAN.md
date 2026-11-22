# VolumeBridge 实施计划（v2，经 Gemini 4 Neon 评审）

目标：单文件 exe 替代 Voicemeeter，让 Windows 音量滑块控制固定音量的 USB 音箱（JBL Flip 6）。

结论：**可行，需修改**（Neon verdict: FEASIBLE-WITH-CHANGES）。下面是修改后的计划，末尾附评审台账。

## 数据流

```
应用 → Windows 默认播放设备 = 虚拟声卡播放端 (Render)
          ↓ 驱动内部回环
虚拟声卡录制端 (Capture) → WASAPI 共享/事件采集 → float32 → 增益(平滑，可直通)
   → 环形缓冲 → 可变比重采样(格式转换 + 漂移补偿) → WASAPI 共享/事件渲染 → Flip 6
音量来源：虚拟声卡 Render 端的 IAudioEndpointVolume（MasterVolumeLevelScalar + Mute）
```

## 关键风险：双重衰减

如果虚拟 Render 端的 `QueryHardwareSupport()` 没有 `ENDPOINT_HARDWARE_SUPPORT_VOLUME` 标志，Windows 音频引擎会在共享模式下**先在软件里施加音量和静音**，再交给驱动。这样回环到录制端的数据已经衰减过了，exe 再乘一次增益就是双重衰减（50% 滑块会变成 25% 幅度）。

处理：`gainMode = auto | apply | passthrough`
- `auto`：根据硬件音量标志和阶段 1 的实测 RMS 来决定。没有硬件音量标志时默认 `passthrough`。
- `apply`：exe 自己乘增益。用于驱动声称有硬件音量、实际却忽略的情况。
- 阶段 1 的 `--probe` 会打印这两项依据（硬件支持标志、实时 RMS），由实测来定。

注意：如果结果是 `passthrough`，"音量控制"其实是 Windows 自己完成的，exe 只负责把声音搬运到 Flip 6，同时处理格式和漂移。需求里的增益公式和平滑过渡只在 `apply` 模式下生效。

## 虚拟声卡选型（已核实）

| 候选 | 实情 | 结论 |
| --- | --- | --- |
| [Virtual Cables](https://github.com/tarekwasfy01/Virtual-Cables)（BSD-2） | 用户态程序模拟 USB Audio Class 1.0 设备，经 [usbip-win2](https://github.com/vadimgrn/usbip-win2) 接入，Windows 使用自带的 usbaudio.sys。usbip-win2 README 称驱动已通过 WHLK 认证或做了 attestation 签名（OSSign）。免费版固定为 48 kHz、16-bit、立体声；作者称免费版存在内存错误 bug，商店付费版已修复。必须保持 Virtual Cables 程序在运行。 | 首选，阶段 1 实测。需确认：开着安全启动和 HVCI 能否安装、是否出现一对端点、滑块是否生效、长期稳定性 |
| [Virtual Audio Wire](https://github.com/HSpear/virtual-audio-wire) | 基于 MSVAD 的内核驱动源码，没有发布任何二进制，需要自行用 WDK 编译并开启测试签名 | **淘汰**（普通用户不可能装） |
| VB-CABLE | 闭源，WHQL 签名，开着安全启动可装。Render 端叫 "CABLE Input"，Capture 端叫 "CABLE Output" | 兜底 |

`--probe` 按 config 中 `virtualCandidates` 的顺序自动查找第一个两端都存在的候选，并报告结果。

## Flip 6 的设备名

USB 模式下 Flip 6 可能显示为 **"Zgmicro AUDIO"**，而不是含 "JBL" 的名称（Neon 的说法，未在用户机器上核实）。因此：
- `outputKeywords` 默认是 `["JBL", "Zgmicro"]`，并且可以用 `outputDeviceId` 精确指定设备。
- 匹配到多个 Render 设备时拒绝启动，要求用设备 ID 消歧。
- **输出设备与虚拟 Render 端是同一设备时拒绝启动**，否则会形成数字反馈回路。

## 技术选型

- .NET 8，`PublishSingleFile` + `SelfContained` + `win-x64` + `IncludeNativeLibrariesForSelfExtract`，**不做 trim**（COM 互操作）。
- **NAudio 只用底层的 `NAudio.CoreAudioApi`**（MMDeviceEnumerator、AudioClient、AudioCaptureClient、AudioRenderClient、AudioClockClient、AudioEndpointVolume）。引擎不使用 `WasapiCapture` 和 `WasapiOut`：它们会丢掉 QPC 时间戳和缓冲区标志，自带的拉取循环也会和我们的漂移控制冲突。采集和渲染各用一个事件驱动线程，等待带 500–1000 ms 超时（防止休眠唤醒后流卡死）。`--probe` 只用于测量，例外地允许使用 `WasapiCapture`。
- 线程注册 MMCSS（`AvSetMmThreadCharacteristicsW("Pro Audio")`），热路径不做内存分配。
- 进程类型：阶段 1 只有 CLI，先用 `Exe`。阶段 4 改为 `WinExe`，带命令行参数时调用 `AttachConsole(ATTACH_PARENT_PROCESS)`，避免登录自启时弹出控制台窗口。
- 托盘：优先用 `Shell_NotifyIconW` 的 P/Invoke（约 150 行），避免 WinForms 带来约 20–30 MB 的体积。

## 重采样与时钟漂移

- 环形缓冲放在重采样器之前（按输入采样率的帧存放）。渲染线程拉数据时，由重采样器按当前比率消耗缓冲。
- 重采样器：带 Kaiser 窗的 windowed-sinc，16–32 抽头，相位表之间线性插值，支持连续变比。它同时负责 44.1↔48 kHz 转换和 ±ppm 的漂移微调。两端都是 48 kHz 时也用它（比率接近 1），保证只有一条代码路径。
- 控制器：先对 `fill_ms` 做约 2 s 的低通滤波，消除 WASAPI 周期造成的 ±10 ms 锯齿抖动，再送入 PI 控制器。目标 30 ms，输出限幅 ±1000 ppm。
  - 抗积分饱和：输出被限幅时冻结积分项。
  - 采集空闲（无数据包或 `AUDCLNT_BUFFERFLAGS_SILENT`）时保持控制器状态不动。
  - 欠载、过载或重新打开设备时重置控制器状态。
- 硬兜底：
  - `fill > target + 40 ms`：一次性丢弃到目标值，做 5 ms 交叉淡化，并计入过载次数。
  - `fill < 5 ms`：进入 `Priming` 子状态，输出静音（带淡出），直到 `fill >= target` 才恢复，并计入欠载次数。

## 增益（仅 apply 模式）

- 按需求：`dB = 20·log10(max(scalar, 0.001))`，换算成线性值就是 `gain = max(scalar, 0.001)`，代码里直接这样写，并用 `minDb`/`maxDb` 钳位。
- Windows 的 scalar 本身已经过 audio taper 映射。如果实测发现低段下降过快，可以改用 `GetMasterVolumeLevel()` 读取 Windows 原生的 dB 值，配置项为 `curve: scalar | nativeDb`。
- 静音时目标增益为 0。增益按样本做线性斜坡，时长 `smoothingMs`（默认 20）。COM 回调线程只写一个 volatile 目标值。

## 延迟

- 实时管线延迟 = 采集包年龄（`qpc_now - capture_qpc`，遇到 `TIMESTAMP_ERROR` 或 0 时跳过）+ 环形缓冲填充时长 + 渲染端 padding（`GetCurrentPadding / rate`）。每 10 s 记录一次 min/avg/max。
- 打开设备时记录一次两端的 `GetStreamLatency()`，作为静态参考值。
- 声学延迟（USB 传输加音箱内部 DSP）需要外接麦克风测量，属于可选项，不计入日志数值。

## 健壮性

- 状态机：`Idle → Opening → Running(Priming/Streaming) → Faulted → 退避 → Opening`。
- `IMMNotificationClient` 收到的事件投递到单一控制线程，在那里重建 COM 对象。
- `AUDCLNT_E_DEVICE_INVALIDATED`、`AUDCLNT_E_DEVICE_IN_USE`（被其他程序独占）、事件等待超时、`WM_POWERBROADCAST` 唤醒消息：都转入 Faulted，按 500 ms 到 5 s 指数退避后重开。每次重开都重新查询 `GetMixFormat()`，不缓存格式。
- 渲染会话调用 `SetDuckingPreference(TRUE)`，退出 Windows 通话时的自动闪避（ducking）。
- 输出不可用时，采集数据直接丢弃，环形缓冲有固定上限。
- 用命名 Mutex 防止多开。检测到 `voicemeeter*.exe` 进程时警告并退出（加 `--force` 可跳过）。
- config 和日志路径一律基于 `AppContext.BaseDirectory`（计划任务启动时的工作目录是 System32）。

## 日志、配置、部署

- 日志 `volumebridge.log`，与 exe 同目录，按 2 MB × 3 个文件滚动。每 60 s 输出一次统计：欠载数、过载数、ppm、填充量、延迟。
- `config.json` 不存在时自动生成默认文件。
- `--install-autostart`：用 Task Scheduler 的 XML 方式（`schtasks /Create /XML`）创建任务，避免路径中的空格和引号问题。触发器为 ONLOGON，权限为 LIMITED。`--uninstall-autostart` 删除该任务。
- 退出时停止所有流、释放 COM 和 Mutex。不修改系统默认设备，不留残留状态。

## 环境分工

- 本机（Linux）：DSP 核心和单元测试、编译、`dotnet publish -r win-x64` 交叉发布、CI。
- 用户（Windows）：驱动安装、`--probe` 实测、全部验收测试。

## 里程碑

1. **M0（本次）**：仓库、`--probe`、CI 发布 exe、阶段 1 测试步骤 → 用户实测 → 写 `docs/PHASE1-REPORT.md` → **等用户确认**。
2. M1：引擎跑通（底层 WASAPI，先做直通，暂不补偿漂移）。
3. M2：重采样、漂移控制、延迟统计，附 DSP 单元测试。
4. M3：健壮性和日志。
5. M4：托盘、自启动、WinExe、正式发布。

## Neon 评审台账（第 1 轮，gemini-4-neon，thought_level=medium）

| ID | 级别 | 结论 |
| --- | --- | --- |
| N1 | blocker | **部分接受**。VAW 淘汰（核实无二进制发布）。Virtual Cables 保留首选：Neon 称 usbip-win 未签名，但 usbip-win2 README 写明已获 WHLK/attestation 签名，Virtual Cables 使用的正是 usbip-win2，最终以阶段 1 实测为准 |
| N2 | blocker | 接受，新增 gainMode 与硬件音量标志检测 |
| N3 | blocker | 接受，probe 列出全部设备，支持多关键字和设备 ID |
| N4 | blocker | 接受，引擎只用 NAudio 底层 CoreAudioApi |
| N5 | blocker | 接受，拒绝反馈回路和多重匹配 |
| N6 | should | 接受（抗积分饱和、空闲保持、Priming、状态重置） |
| N7 | should | 接受，代码里直接用 scalar，另提供 `curve` 选项 |
| N8 | should | 接受，按新公式计算延迟 |
| N9 | should | 接受（阶段 4 处理 WinExe 和托盘体积） |
| N10 | should | 接受（休眠唤醒、MMCSS、ducking、独占模式、格式变化） |
| N11 | nit | 接受（BaseDirectory、XML 方式建任务） |
