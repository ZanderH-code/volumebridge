**English** | [中文](README.zh-CN.md)

# VolumeBridge

**Make the Windows volume slider and mute key control a USB speaker that ignores system volume — without Voicemeeter.**

Some USB speakers and USB audio interfaces play at a fixed level on Windows. The JBL Flip 6 in USB audio mode is one example: 1 % and 100 % sound the same. The usual workaround is Voicemeeter sitting in between to apply the volume. VolumeBridge does just that one job, with one installer and one tray icon.

## How it works

```
Apps ──▶ "VolumeBridge Speaker" (a USB speaker emulated by VolumeBridge; set as default output)
           │  Windows sends it volume and mute in dB, exactly as to a real USB speaker
           ▼
       VolumeBridge: applies that gain (20 ms ramp) ──▶ your USB speaker
```

- VolumeBridge emulates a standard USB Audio Class 1.0 device (48 kHz / 16-bit / stereo) over USB/IP, and Windows' built-in USB audio driver picks it up. The device declares a hardware volume control, so Windows passes the volume to VolumeBridge instead of turning the sound down itself. **The volume is never applied twice.**
- The virtual USB host comes from [usbip-win2](https://github.com/vadimgrn/usbip-win2), a signed open-source driver that the installer sets up for you. VolumeBridge ships no driver of its own.
- **Quality:** when the speaker runs at 48 kHz, samples pass through unchanged apart from the gain. Small clock differences between the two devices are absorbed by trimming the virtual speaker's clock, never by resampling. For other output rates a 64-tap Kaiser-windowed sinc resampler is used (≈80 dB stopband).
- **Latency:** VolumeBridge's own buffer is about 20 ms by default. Including Windows and USB, expect roughly 30–50 ms in total, against the 150–250 ms that is typical for Bluetooth audio.

## Compatibility

- **Tested:** JBL Flip 6 in USB audio mode (shows up as "Speakers (Zgmicro AUDIO)").
- **In principle:** any USB speaker or USB audio interface that shows up as a Windows playback device but ignores system volume. Pick it under "Output device" in the window. By default, a device whose name contains `JBL` or `Zgmicro` is selected automatically.
- Whether other JBL models work depends on their USB port acting as an audio device. Some models' USB ports only charge, and those cannot work. Reports are welcome as issues.
- Requirements: Windows 10 1903+ or Windows 11, x64. Installing needs administrator rights; the app itself runs unelevated.

## Install and use

1. Download `VolumeBridgeSetup.exe` from [Releases](../../releases/latest) and run it.
   - It installs the usbip-win2 driver; USB devices drop out for a few seconds while it does, which is expected.
   - If it asks for a restart, you can try without restarting first.
2. **Quit Voicemeeter.** It holds the speaker exclusively, so the two cannot run at the same time.
3. Open VolumeBridge and click **"设为默认输出"** (Set as default output).
4. Tick **"开机自动启动"** (Start at logon). Closing the window keeps VolumeBridge running in the tray; right-click the tray icon to quit.
5. **"检查更新"** (Check for updates) downloads the latest release, verifies its SHA-256 and installs it. This needs one administrator confirmation.

The user interface is in Chinese for now.

Config and logs live in `%LOCALAPPDATA%\VolumeBridge` ("打开日志文件夹" in the window opens it). Settings in `config.json`:

| Key | Default | Meaning |
| --- | --- | --- |
| `outputKeywords` | `["JBL","Zgmicro"]` | Name keywords for automatic output selection |
| `outputDeviceId` | `null` | Fixed output device (set by the drop-down) |
| `targetBufferMs` | 20 | Buffer target; raise to 30–40 if you hear dropouts |
| `outputBufferMs` | 10 | WASAPI buffer on the speaker side |
| `minDb` / `maxDb` / `volumeStepDb` | -60 / 0 / 0.25 | Volume range and step offered to Windows |
| `smoothingMs` | 20 | Gain ramp length |
| `usbipPort` | 3240 | Local USB/IP port |

## Limitations

- Depends on the third-party usbip-win2 driver and its stability.
- No mixer, routing or the other Voicemeeter features.
- Not handled yet: Windows communications ducking; after sleep, the connection takes about 30 s to come back.
- Uninstall asks whether to remove usbip-win2 as well (other software may use it); removing the driver needs a reboot.

## Building

```
dotnet test tests/VolumeBridge.Tests
dotnet publish src/VolumeBridge -c Release -r win-x64 -o out
```

Cross-compiling from Linux works. CI (`.github/workflows/build.yml`) builds the Inno Setup installer on Windows and publishes a release for every `v*` tag. Design notes and review history are in [docs/PLAN.md](docs/PLAN.md) (Chinese); test steps are in [docs/TEST.md](docs/TEST.md).

## Credits and licenses

- The UAC1 device and USB/IP server are adapted from [Virtual Cables](https://github.com/tarekwasfy01/Virtual-Cables) (BSD-2-Clause).
- [usbip-win2](https://github.com/vadimgrn/usbip-win2) (BSD-2-Clause) is bundled unmodified.
- [NAudio](https://github.com/naudio/NAudio) (MIT).
- Full third-party license texts are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). A license for VolumeBridge itself has not been chosen yet.
