# VolumeBridge

用单文件 exe 替代 Voicemeeter，让 Windows 音量滑块和静音键能控制一台不响应系统音量的 USB 音箱（例如 USB 音频模式下的 JBL Flip 6）。

```
应用 → 虚拟声卡（默认播放设备） → VolumeBridge 采集 → 增益/格式转换/漂移补偿 → Flip 6
```

不自带驱动，虚拟声卡使用现成项目：首选 [Virtual Cables](https://github.com/tarekwasfy01/Virtual-Cables)，兜底用 VB-CABLE。

## 状态

**阶段 1：验证中。** 目前只有 `--probe` 诊断模式，音频引擎尚未实现。

- 计划与可行性评审：[docs/PLAN.md](docs/PLAN.md)
- 阶段 1 测试步骤：[docs/PHASE1.md](docs/PHASE1.md)

## 使用

```
VolumeBridge.exe --probe [--seconds N]
```

每 200 ms 打印一次：虚拟声卡播放端的音量标量和静音状态、录制端实际收到的电平、输出设备的音量和格式。设备变化时打印完整设备清单。输出同时写入 exe 旁边的 `probe.log`。首次运行时会在 exe 旁边生成 `config.json`。

## 构建

```
dotnet test tests/VolumeBridge.Tests
dotnet publish src/VolumeBridge -c Release -r win-x64 -o out
```

在 Linux 上也可以交叉发布。CI 在每次推送到 main 时生成 `VolumeBridge-win-x64` 工件。
