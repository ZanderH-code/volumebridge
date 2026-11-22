# VolumeBridge

用单文件 exe 替代 Voicemeeter，让 Windows 音量滑块和静音键能控制一台不响应系统音量的 USB 音箱（例如 USB 音频模式下的 JBL Flip 6）。

```
应用 → "VolumeBridge Speaker"（VolumeBridge 通过 USB/IP 模拟的 USB 音箱）
     → Windows 音量以 dB 下发给它 → 增益/格式转换/时钟跟随 → Flip 6
```

不自己写驱动：虚拟 USB 主控使用已签名的 [usbip-win2](https://github.com/vadimgrn/usbip-win2)，随安装包分发。UAC1 设备模拟移植自 [Virtual Cables](https://github.com/tarekwasfy01/Virtual-Cables)（BSD-2）。

## 状态

**M1 待测：** `--speaker` 模式会模拟 "VolumeBridge Speaker" 并记录 Windows 发来的音量和静音，暂不转发声音到音箱。测试步骤见 [docs/M1-TEST.md](docs/M1-TEST.md)。

- 计划与可行性评审：[docs/PLAN.md](docs/PLAN.md)

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
