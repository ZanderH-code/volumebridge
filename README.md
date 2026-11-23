**English** | [中文](README.zh-CN.md)

# VolumeBridge

**Make the Windows volume slider and mute key control a USB speaker that ignores system volume — without Voicemeeter.**

Some USB speakers and USB audio interfaces play at a fixed level on Windows. The JBL Flip 6 in USB audio mode is one example: 1 % and 100 % sound the same. The usual workaround is Voicemeeter sitting in between to apply the volume. VolumeBridge does just that one job, with one installer and one tray icon.

## How it works

**Zero-latency mode (default)**

```
Apps ──▶ your USB speaker (default output)
           └ VolumeBridge volume plugin (APO) in its effect chain: follows the Windows slider and mute, in place, no buffering
```

- The plugin sits in the speaker's own effect chain (endpoint effect, EFX) and multiplies the audio by the slider value inside the Windows audio engine, with a 20 ms ramp and **no added latency**.
- Volume source: Windows 11 APO volume notifications, with the tray app passing the slider value as a fallback.
- **Trade-offs:** the plugin is unsigned, so Windows' protected audio path is turned off (`DisableProtectedAudioDG=1`); a few DRM players may be affected. The speaker's "Audio enhancements" must stay on. Apps using exclusive mode bypass the plugin. Everything is restored on uninstall.

**Compatible mode (switch in the window)**

```
Apps ──▶ "VolumeBridge Speaker" (USB speaker emulated by VolumeBridge) ──▶ gain from Windows ──▶ your USB speaker
```

- Emulates a standard USB speaker over USB/IP using the [usbip-win2](https://github.com/vadimgrn/usbip-win2) driver (optional at install). Leaves the system audio settings alone but adds about 40–60 ms of latency.

## Compatibility

- **Tested:** JBL Flip 6 in USB audio mode (shows up as "Speakers (Zgmicro AUDIO)").
- **In principle:** any USB speaker or USB audio interface that shows up as a Windows playback device but ignores system volume. Pick it under "Output device" in the window. By default, a device whose name contains `JBL` or `Zgmicro` is selected automatically.
- Whether other JBL models work depends on their USB port acting as an audio device. Some models' USB ports only charge, and those cannot work. Reports are welcome as issues.
- Requirements: Windows 10 1903+ or Windows 11, x64. Installing needs administrator rights; the app itself runs unelevated.

## Install and use

1. Connect the speaker and **quit Voicemeeter** (it holds the speaker exclusively).
2. Download `VolumeBridgeSetup.exe` from [Releases](https://github.com/ZanderH-code/volumebridge/releases/latest) and run it, keeping "Enable the zero-latency volume plugin" ticked. Windows audio pauses for about 2 seconds during setup.
3. Open VolumeBridge, check that it says "Ready / Working (zero latency)", then click **Set as default output** to make the speaker the default.
4. Tick **Start at logon**. Closing the window keeps VolumeBridge running in the tray; right-click the tray icon to quit.
5. **Check for updates** downloads the latest release, verifies its SHA-256 and installs it. This needs one administrator confirmation.

The installer asks for English or 简体中文; the app follows that choice and can be switched in its window.

Config and logs live in `%LOCALAPPDATA%\VolumeBridge` (**Open log folder** in the window opens it). Settings in `config.json`:

| Key | Default | Meaning |
| --- | --- | --- |
| `outputKeywords` | `["JBL","Zgmicro"]` | Name keywords for automatic output selection |
| `outputDeviceId` | `null` | Fixed output device (set by the drop-down) |
| `targetBufferMs` | 20 | Buffer target; raise to 30–40 if you hear dropouts |
| `outputBufferMs` | 10 | WASAPI buffer on the speaker side |
| `minDb` / `maxDb` / `volumeStepDb` | -60 / 0 / 0.25 | Volume range and step offered to Windows |
| `smoothingMs` | 20 | Gain ramp length |
| `mode` | `apo` | `apo` zero-latency mode; `virtual` compatible mode |
| `usbipPort` | 3240 | Local USB/IP port (compatible mode) |
| `language` | `auto` | `zh` / `en`; `auto` = installer choice, else Windows language |

## Roadmap

- WASAPI exclusive-mode output to the speaker (lower latency, bit-exact 16-bit).
- Smaller input buffer that grows automatically only when needed.
- Virtual speaker as a USB Audio 2.0 high-speed device (1 ms packets, feedback-endpoint clocking).

## Limitations

- **If sound stops after installing:** uninstall VolumeBridge in Settings → Apps (restores the original settings), or set the speaker's "Audio enhancements" to Off (Windows then skips the plugin).

- The compatible mode depends on the third-party usbip-win2 driver.
- Windows feature updates or moving the speaker to another USB port may drop the plugin setting; the window says so, and "Enable plugin" fixes it.
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
- Full third-party license texts are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). VolumeBridge itself is released under the [MIT License](LICENSE).
