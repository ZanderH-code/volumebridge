# VolumeBridge 实施计划（v3：自带虚拟 USB 音箱，经 Neon 评审修订）

目标：一个安装包，装完后 Windows 音量滑块和静音键就能控制 JBL Flip 6（USB 音频模式下它不响应系统音量）。

v3 相对 v2 的变化：由用户选定（2026-11-22）。VolumeBridge 不再依赖外部虚拟声卡程序，而是**自己模拟一个 USB 音箱**。v2 的评审结论中，适用于输出端（WASAPI 渲染、健壮性、日志）的部分继续保留，见文末。

## 架构

```
应用 → Windows 默认播放设备 = "VolumeBridge Speaker"
         （usbaudio.sys ← usbip-win2 虚拟 USB 主控 ← TCP 127.0.0.1:3240）
              ↓ 同步 OUT 的 isochronous URB：48 kHz / 16-bit / 立体声 PCM
VolumeBridge.exe：USB/IP 服务端 + UAC1 设备模拟
              ├─ 控制请求：Feature Unit 的 SET_CUR VOLUME（1/256 dB）和 MUTE → 目标增益
              ↓
         环形缓冲 → 增益（平滑） → 格式转换/重采样 → WASAPI 共享/事件渲染 → Flip 6
```

- 这种做法参考了 [Virtual Cables](https://github.com/tarekwasfy01/Virtual-Cables)（BSD-2，Go，`src/internal/{uac1,usbip}` 约 1000 行），把它的 UAC1 描述符和 USB/IP 服务端移植到 C#。只保留**播放路径**，去掉录制路径。以下 4 处必须同时修改，否则 usbaudio 会报错误代码 10：
  - 配置描述符的 `bNumInterfaces=2`；
  - AC Header 改为 `bLength=9, wTotalLength=40, bInCollection=1, baInterfaceNr={1}`；
  - `OP_REP_DEVLIST` 只写 2 个接口元组；
  - `writeUSBDevice` 的接口数改为 2。

  另外，`SetInterface`、端点和实体（entity）的处理只接受接口 1、EP 0x01、Feature Unit 2。
- 设备身份：VID 0xFFFF，PID 用 **0x5642**（不和 Virtual Cables 的 0xCA0n 冲突）；`iSerialNumber` 固定为 `VOLBRIDGE001`。这样 Windows 按 VID+PID+序列号识别设备实例，端点 ID 在重启后保持不变，Windows 能记住默认设备，也不会出现 "2- VolumeBridge Speaker" 这样的重复设备。
- 每个设备只允许一个 USB/IP 导入会话。新的 `OP_REQ_IMPORT` 到来时，先关闭旧连接。
- usbip-win2（[vadimgrn/usbip-win2](https://github.com/vadimgrn/usbip-win2)，BSD-2，驱动有 WHLK 认证或 attestation 签名）提供虚拟 USB 主控驱动和 `usbip.exe`，随安装包一起分发。
- 不需要录制端，也不需要第二个后台程序。

## 音量：以"真硬件音量"的方式实现

- 描述符里声明 Feature Unit，带 Mute 和 Volume 控制（`bmaControls=0x03`）。usbaudio.sys 会把它报告为 `ENDPOINT_HARDWARE_SUPPORT_VOLUME|MUTE`。这样 Windows **不在软件里调小声音**，而是把滑块换算成 dB，用 `SET_CUR` 发给设备，和真实 USB 音箱完全一样。Virtual Cables 收到这些值只存起来、不使用，这正是它的滑块无效的原因。
- 我们收到 `SET_CUR VOLUME` 后，按 `gain = 10^(dB/20)` 设置目标增益；收到 `MUTE=1` 时增益为 0。增益变化时做 20 ms 线性斜坡过渡。
- dB 范围通过 `GET_MIN/MAX/RES` 告诉 Windows，默认 -60…0 dB，**步长 0.25 dB**（`0x0040`；如果是 1 dB，按音量键每次调 2% 会显得粗糙）。范围可配置，对应 config 的 `minDb`/`maxDb`。必须满足 `(max-min) % res == 0`；初始的 `GET_CUR` 必须落在区间内。
- `0x8000`（-32768）在 UAC1 里表示 -∞ dB，此时增益为 0。其他值钳位到 `[minDb, maxDb]`。
- Windows 会把滑块映射到这个区间。阶段 1 要实测这个映射：100%→1% 时下发的 dB 值、听感是否连续。
- 和需求公式的关系：需求里的 `dB = 20·log10(max(scalar, 0.001))` 是在 exe 自己读 scalar 时用的。v3 直接使用 Windows 下发的 dB 值，更接近 Windows 对真实硬件的行为。如果实测曲线不理想，保留一个备选：用 `IAudioEndpointVolume` 读取 "VolumeBridge Speaker" 的 scalar，再按需求公式计算增益（配置项 `volumeSource: device | scalar`）。
- 不存在声音被调小两次的问题。

## 时钟

- 在 USB/IP 方式下，设备的时钟其实就是**我们完成 isochronous URB 的节奏**。Windows 一次排队多个 URB（每个约 10 ms）。usbip-win2 强制使用 `USBD_START_ISO_TRANSFER_ASAP`，并且没有实现帧号查询，所以真正起作用的只有完成时间。
- 节奏用**单调高精度时钟**驱动：`Stopwatch` 加上 `CreateWaitableTimerExW(CREATE_WAITABLE_TIMER_HIGH_RESOLUTION)`。不能用 `Thread.Sleep`/`Task.Delay`，它们 15.6 ms 的精度会导致 URB 断流。每个 URB 按每个包 1 ms 预约连续的截止时间，沿用 Virtual Cables 的 `reserveIsoCompletion` 思路。
- **漂移：用锁相环（PLL）微调这个时钟的速率。** Flip 6 正常播放时，比较它实际消耗的帧数和经过的墙钟时间，把单调时钟的速率在 ±500 ppm 内平滑地调向 Flip 6 的时钟。这样 Windows 送声音的速度会跟着 Flip 6 走，原则上不需要重采样。
- **Flip 6 拔出、暂停、被独占或刚从睡眠唤醒时，PLL 保持 0 ppm**，也就是纯墙钟。URB 照常按时完成，声音丢弃，**绝不能让 Windows 音频引擎卡住**（Neon N1：如果直接按 Flip 6 的播放回调来完成 URB，Flip 6 不可用时引擎会卡死）。
- **兜底：** 保留 v2 的环形缓冲 + PI 控制器 + 可变比重采样器。如果 PLL 微调节奏在实测中出现问题，就固定为墙钟，改用重采样补偿漂移。
- 格式：虚拟设备固定为 48 kHz / 16-bit / 立体声。Flip 6 的混音格式在打开时查询；不是 48 kHz 时用重采样器转换。
- 目标缓冲可配置，最低可设到 15 ms，默认 30 ms。

## 生命周期与权限

- **不需要管理员权限运行**（Neon N3，已核实）：usbip-win2 0.9.8.1 的 VHCI 设备对象 SDDL 是 `SDDL_DEVOBJ_SYS_ALL_ADM_RWX_WORLD_RW_RES_R`（`drivers/ude/vhci.cpp:642`），普通用户就能执行 attach/detach。所以 VolumeBridge 以普通权限运行，登录自启用普通的计划任务即可。阶段 1 要实测确认这一点。只有将来 usbip-win2 收紧权限时，才引入一个最小的提权助手。
- 启动顺序：
  1. 在 127.0.0.1:`port`（默认 3240）监听；
  2. 执行 `usbip.exe --tcp-port <port> attach -r 127.0.0.1 -b 1-1 --once`。`--tcp-port` 是全局选项，必须放在子命令前面；`--once` 表示连不上时不让驱动无限重试。
- **0.9.8.1 不能传 `--serial`**：该版本有已知 bug，修复在发布之后（commit a2a1159）。
- 启动时先清理上次遗留的端口，即 `usbip port` 中指向本机端口的条目。
- **正常退出时必须先 `usbip detach -p <port>`，再关闭监听。** 否则驱动会认为是意外断开，在 30 s 后开始自动重连，最多 20 次（`usbip2_ude.inf` 中的 `ReattachFirstDelay/MaxAttempts`）。
- 进程崩溃时：驱动的自动重连会在 30 s 后尝试。下次启动时先清理旧端口，再重新 attach。
- **睡眠唤醒**：usbip-win2 不处理电源事件（`d0_entry/d0_exit` 为空实现）。所以 VolumeBridge 收到 `PBT_APMRESUMEAUTOMATIC` 后要主动检查 USB/IP 会话和 `usbip port` 的状态，断了就立即重新 attach，同时重新打开 Flip 6 的渲染流。
- 3240 端口被占用时（比如装了 Virtual Cables 或其他 USB/IP 服务端），改用配置中的端口。
- 设备拔插：Flip 6 拔出时，虚拟音箱保留，收到的声音丢弃，缓冲有上限。插回后重新打开渲染流（沿用 v2 的状态机和退避策略）。

## 安装包

- 用 Inno Setup 制作，在 CI 的 windows-latest 上构建，产出 `VolumeBridgeSetup.exe`。安装包本身需要管理员权限（为了装驱动），装好后 VolumeBridge 以普通权限运行。
- 安装步骤：
  1. 安装内置的 usbip-win2 0.9.8.1 x64，已安装同版本或更新版本时跳过。命令为 `USBip-0.9.8.1-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /COMPONENTS="main,client"`，不装它的 GUI、SDK 和 pdb。安装界面要提示用户：**装驱动时所有 USB 3.0 Hub 会重启，USB 设备（包括 Flip 6）会短暂断开**。
  2. 把 VolumeBridge.exe 和默认 config.json 复制到 `%ProgramFiles%\VolumeBridge`；
  3. 注册登录启动的计划任务（普通权限）；
  4. usbip-win2 的安装器带 `AlwaysRestart=yes`。阶段 1 要实测：不重启能否直接 attach。
     - 能：安装后立即启动；
     - 不能：提示用户重启，首次启动和默认设备切换都推迟到重启后登录时进行。
  5. 可选：把 "VolumeBridge Speaker" 设为默认播放设备，通过未公开的 `IPolicyConfig` 接口实现。具体做法：
     - 先用 `IMMNotificationClient` 等到新端点出现，再设置；
     - 设置 `eConsole` 和 `eMultimedia` 两个角色，`eCommunications` 可选；
     - 记录原来的默认设备；
     - 所有调用都"尽力而为"，失败不影响安装和卸载。
- 卸载：
  - 先停止进程（会自动 detach），删除计划任务；
  - 还原默认设备，前提是原设备仍处于活动状态，且不是 VolumeBridge Speaker 本身；
  - 删除文件；
  - usbip-win2 保留还是一并卸载，在卸载时询问用户（其他软件可能也在用它，而且它的卸载也要求重启）。
- 许可证：在安装目录附带 VolumeBridge、usbip-win2、Virtual Cables（移植来源）的许可证声明。

## 风险

1. **usbip-win2 在用户机器上能否正常安装和工作**，包括安全启动、HVCI、睡眠唤醒后的表现。这是整个方案的地基，阶段 1 首先验证。
2. Virtual Cables 作者把这套技术路线标为"实验性"。usbip-win2 旧版本有内存错误问题（作者称新版已修复），所以必须固定使用新版本。
3. 完成节奏跟随 Flip 6 时钟的做法能否被 usbaudio 接受，需要实测，兜底方案是重采样。
4. 延迟：usbaudio 约 10 ms，环形缓冲 15–40 ms，Flip 6 的共享模式缓冲 10–20 ms，**预计约 40–80 ms**。听音乐、看视频没问题，玩游戏能感觉到。以实测为准。
5. 本地 TCP 端口暴露给本机的其他进程（只绑定 127.0.0.1；单会话；进程以普通权限运行）。
6. 移植工作量：USB/IP 协议、UAC1 请求、iso 时序，约 1000–1500 行 C# 和对应的单元测试。协议部分可以在 Linux 上用录制的 URB 字节序列做单元测试。

## 阶段与里程碑

1. **阶段 1（验证）**：
   用户选择等 M1 完成后，直接用 VolumeBridge 自带的虚拟音箱来验证。验证项目：
   - usbip-win2 能否在安全启动和 HVCI 下安装，装完是否需要重启；
   - 普通权限能否 attach；
   - 设备能否出现，端点是否报告 `hw=[vol,mute]`；
   - 滑块 100%→1% 和静音时，设备收到的 dB 和静音值；
   - 声音电平是否正常；
   - 重启 VolumeBridge 后端点 ID 是否不变；
   - 睡眠唤醒后能否恢复。
2. **M1**：C# 版 USB/IP 服务端 + UAC1 播放设备 + attach/detach + 高精度节奏器。把收到的音量、静音、声音电平和 URB 统计写进日志，还不输出到 Flip 6。另附一个手动的 `M1-TEST.md`：先手动安装 usbip-win2，再运行 exe。
3. **M2**：输出到 Flip 6，包括增益、格式转换、时钟跟随（兜底为重采样）、延迟统计。
4. **M3**：健壮性：拔插、睡眠唤醒、崩溃恢复、日志。
5. **M4**：安装包、计划任务、默认设备切换与还原、托盘。

## 从 v2 保留的结论（Neon 第 1 轮评审）

- 输出端只用 NAudio 的底层 CoreAudioApi，不使用 `WasapiOut`。线程注册 MMCSS，热路径不做内存分配，事件等待设有限超时。
- Flip 6 可能显示为 "Zgmicro AUDIO"。输出设备按关键字或设备 ID 匹配，匹配到多个时拒绝启动；**输出设备是 VolumeBridge Speaker 自身时拒绝启动**（防止声音自我循环）。
- 每次打开设备都重新查询 `GetMixFormat()`。渲染会话退出 Windows 通话自动闪避（ducking）。处理 `DEVICE_INVALIDATED`、`DEVICE_IN_USE` 和唤醒事件。
- config 和日志路径基于 `AppContext.BaseDirectory`。WinExe + AttachConsole。托盘用 `Shell_NotifyIconW`。

v2 中针对外部虚拟声卡的结论（VB-CABLE 兜底、gainMode 自动判断、录制端 RMS）不再适用。`--probe` 保留为诊断工具。

## Neon 评审台账（v3，第 1 轮，gemini-4-neon，thought_level=medium，结论：FEASIBLE-WITH-CHANGES）

| ID | 级别 | 结论 |
| --- | --- | --- |
| N1 | blocker | 接受：改为墙钟 + PLL 微调，Flip 6 不可用时保持 0 ppm，使用高精度定时器 |
| N2 | blocker | 接受：去掉录制路径时 4 处同步修改 |
| N3 | should | 接受（已核实 `vhci.cpp:642` 的 SDDL）：以普通权限运行 |
| N4 | should | 接受：`0x8000` 表示静音，步长改为 0.25 dB，校验区间约束 |
| N5 | should | 接受（已核实 `usbip.cpp:178` 全局选项、`inf:78-80` 自动重连）：`--tcp-port` 放在子命令前，加 `--once`，不传 `--serial`，退出时 detach |
| N6 | should | 接受（已核实 `setup.iss:112` 及组件定义）：静默安装只装 main 和 client，提示 Hub 会重启，处理需要重启的情况 |
| N7 | should | 接受：唤醒后主动检查并重新 attach |
| N8 | should | 接受：PID 0x5642，固定序列号，`IPolicyConfig` 等待端点出现后再设置、按条件还原 |
| N9 | should | 接受：单会话 |
| N10 | nit | 接受：延迟预期改为 40–80 ms |
