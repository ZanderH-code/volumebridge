**English** | [中文](README.zh-CN.md)

# VolumeBridge

**JBL Flip 6 USB volume not working, stuck at max, or too loud on Windows?** VolumeBridge makes the Windows volume slider, keyboard volume keys, and mute button control your USB speaker again — with zero added latency and no Voicemeeter.

> ✅ **Tested on:** JBL Flip 6 in USB audio (wired) mode (`Speakers (Zgmicro AUDIO)`) on Windows 11 (26H2).
> Older JBL models (Flip 5, Flip 4, Charge, Pulse, Xtreme, etc.) and other USB speakers or USB DACs that ignore Windows volume should work in theory if they connect as a USB audio device, though they are untested. Reports are welcome in [Issues](https://github.com/ZanderH-code/volumebridge/issues).

## The problem: USB speaker stuck at full volume

If this matches what you see, VolumeBridge is built for it:

- Your JBL Flip 6 is connected to a Windows PC over USB in USB audio (wired) mode, and **it plays at fixed maximum volume**.
- The Windows volume slider, volume keys, and **mute** have no effect — even at 1% or muted, it stays loud.
- The speaker's own volume buttons do not work in USB audio mode either.
- Windows lists the device as **"Speakers (Zgmicro AUDIO)"** or **Generic USB Audio**.
- The usual workaround is **Voicemeeter**, which adds latency, takes the speaker exclusively, and is cumbersome to configure just for volume control.

**Why this happens:** JBL's wired USB audio mode is a vendor demo/service mode whose firmware locks the speaker at maximum volume. The speaker tells Windows that it handles volume in hardware, so Windows sends the volume level without lowering the audio itself — and the speaker ignores that value.

VolumeBridge attenuates the audio inside the Windows audio engine before it reaches the speaker, following the Windows volume slider and mute state with a 20 ms ramp so there are no clicks.

## Install and use

**Requirements:** Windows 10 (1903+) or Windows 11, 64-bit (x64). Administrator rights are needed to install; the app itself runs without elevation.

1. **Switch your speaker to USB audio mode** and connect it to your PC. On the JBL Flip 6, hold **Bluetooth + Play for about 10 seconds** (check your model's instructions if using another speaker). **Quit Voicemeeter** if it is running so it does not hold the speaker exclusively.
2. **Download and run `VolumeBridgeSetup.exe`** from the [latest release](https://github.com/ZanderH-code/volumebridge/releases/latest). Keep **"Enable the zero-latency volume plugin"** ticked. Windows audio will pause for about 2 seconds during setup.
3. **Open VolumeBridge** and click **Set as default output**.
4. **Check the status and enable startup:** play audio and confirm the window shows **● Working (zero latency)**, then tick **Start at logon**.

### Everyday controls

- **Max volume cap:** Defaults to **-12 dB** so 100% on the Windows slider is not deafening. Adjustable in the window from **0 to -30 dB**.
- **Output device picker:** Automatically picks a playback device whose name contains `JBL` or `Zgmicro`, or you can select another device manually.
- **Window and tray:** Closing the window minimizes VolumeBridge to the system tray so it stays out of the way.
- **Language:** Choose English or 简体中文 in the installer, and switch anytime in the app.
- **Updates:** One-click update check from GitHub Releases (downloads are SHA-256 verified).
- **Clean uninstall:** Uninstalling from Settings → Apps restores all original Windows audio settings.

## Trade-offs and troubleshooting

Zero-latency mode modifies one Windows audio setting so the plugin can load. Know these trade-offs before installing:

- **Protected audio path is turned off (`DisableProtectedAudioDG=1`):** The plugin is not signed by Microsoft, so Windows needs this setting to load it (Equalizer APO does the same). A few DRM players (such as certain protected 4K audio streams) may be affected.
- **Keep Audio enhancements on:** In **Settings → System → Sound → [your speaker]**, **Audio enhancements** must stay set to **Device Default Effects**. Turning it off makes Windows skip the plugin.
- **Exclusive mode bypasses the plugin:** Apps that take exclusive WASAPI control bypass the Windows audio engine and will not be attenuated.
- **USB port changes or Windows feature updates:** Moving the speaker to a different USB port or installing a major Windows update can drop the plugin setting. The VolumeBridge window will tell you, and clicking **Enable plugin** restores it.
- **If sound stops working after installing:** Uninstall VolumeBridge in **Settings → Apps** (which restores everything), or set the speaker's **Audio enhancements** to **Off**.

## Supported devices

| Device | Status | Notes |
| --- | --- | --- |
| JBL Flip 6 (USB audio mode, "Speakers (Zgmicro AUDIO)") | ✅ Tested | Verified on Windows 11 (26H2) |
| Older JBL speakers (Flip 5, Flip 4, Charge, Pulse, Xtreme, …) | Untested | Should work in theory if the speaker connects as a USB audio device. Models whose USB port only charges cannot work. |
| Other USB speakers / USB DACs with fixed volume | Untested | Should work in theory — choose the device in the **Output device** picker. |

If you test another speaker or DAC, please share your result in [Issues](https://github.com/ZanderH-code/volumebridge/issues).

## FAQ

**Why not just use Voicemeeter?**
Voicemeeter routes audio through a virtual cable and a second output, which adds latency, uses more resources, locks the speaker exclusively, and requires manual routing. VolumeBridge's zero-latency mode processes audio in place with no added latency and almost no setup.

**How is this different from Equalizer APO?**
Both use a Windows Audio Processing Object (APO). Equalizer APO applies a fixed Preamp value from a config file and does not follow the Windows volume slider. VolumeBridge's plugin follows the Windows slider and mute key directly.

**Does VolumeBridge need to stay running in the background?**
When Windows 11 delivers APO volume notifications to the plugin, the plugin works on its own. Keeping **Start at logon** enabled is still recommended because the tray app acts as a fallback if notifications are not delivered, and it uses minimal resources.

## How it works

VolumeBridge includes two modes. You can switch between them in the app window.

| Mode | How it works | Added latency | System audio changes |
| --- | --- | --- | --- |
| **Zero-latency (default)** | Endpoint-effect APO plugin inside the speaker's own effect chain | None (in-place) | Sets `DisableProtectedAudioDG=1`; restored on uninstall |
| **Compatible (fallback)** | USB/IP virtual speaker (`VolumeBridge Speaker`) forwarding to the real speaker | ~40–60 ms | None (uses optional `usbip-win2` driver) |

### Zero-latency mode (default)

```text
Apps ──▶ Your USB speaker (default output)
           └ VolumeBridge volume plugin (EFX APO in audiodg): applies slider × max-volume cap in place (20 ms ramp)
```

- Runs as an unsigned endpoint-effect Audio Processing Object (EFX APO) inside the Windows audio engine (`audiodg`).
- Reads volume from Windows 11 APO volume notifications; the tray app also passes the slider state via shared memory as a fallback.
- Coexists with the speaker's existing **Microsoft Audio Home Theater Effects**.

### Compatible mode (fallback)

```text
Apps ──▶ "VolumeBridge Speaker" (USB/IP virtual speaker) ──▶ Software gain ──▶ Your USB speaker
```

- Emulates a standard USB audio device over USB/IP using [usbip-win2](https://github.com/vadimgrn/usbip-win2) (available as an optional task in the installer).
- Leaves Windows audio settings untouched, at the cost of ~40–60 ms of latency.

## Configuration and logs

- **Config and app logs:** `%LOCALAPPDATA%\VolumeBridge` (click **Open log folder** in the window).
- **Plugin logs:** `C:\ProgramData\VolumeBridge`.

Settings in `%LOCALAPPDATA%\VolumeBridge\config.json`:

| Key | Default | Description |
| --- | --- | --- |
| `mode` | `"apo"` | `"apo"` for zero-latency mode; `"virtual"` for compatible mode |
| `maxOutputDb` | `-12` | Output level at 100% slider (dB); `0` = speaker's full level |
| `outputKeywords` | `["JBL", "Zgmicro"]` | Case-insensitive name keywords for automatic device selection |
| `outputDeviceId` | `null` | Fixed output endpoint ID (set by the device picker) |
| `language` | `"auto"` | `"en"`, `"zh"`, or `"auto"` (follows installer choice) |
| `targetBufferMs` / `outputBufferMs` | `20` / `10` | Compatible-mode audio buffering (ms) |
| `minDb` / `maxDb` / `volumeStepDb` | `-60` / `0` / `0.25` | Compatible-mode volume range and step advertised to Windows |
| `usbipPort` | `3240` | Compatible-mode local USB/IP TCP port |

## Building from source

```powershell
dotnet test tests/VolumeBridge.Tests
dotnet publish src/VolumeBridge -c Release -r win-x64 -o out
```

The GitHub Actions workflow (`.github/workflows/build.yml`) builds the C++ APO (`apo/`) and the Inno Setup installer on Windows, and publishes a GitHub Release on every `v*` tag. Design notes and history are in [docs/PLAN.md](docs/PLAN.md) (Chinese).

## Credits and licenses

- Compatible mode's UAC1 device and USB/IP server are adapted from [Virtual Cables](https://github.com/tarekwasfy01/Virtual-Cables) (BSD-2-Clause).
- [usbip-win2](https://github.com/vadimgrn/usbip-win2) (BSD-2-Clause) is optionally bundled, unmodified.
- [NAudio](https://github.com/naudio/NAudio) (MIT).
- APO registration follows the public behaviour of [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (no GPL code is copied).
- Full third-party notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). VolumeBridge is licensed under the [MIT License](LICENSE).

<sub>Keywords: JBL Flip 6 USB volume not working, JBL Flip 6 PC volume stuck at max, JBL Flip 6 USB-C audio too loud, Windows 11 USB speaker volume slider does nothing, mute not working USB audio, Speakers Zgmicro AUDIO, Generic USB Audio fixed volume, JBL demo mode service mode, Flip 5 Flip 4 Charge USB audio volume, USB DAC fixed volume Windows 11, Voicemeeter alternative, Equalizer APO follow Windows volume slider, volume APO plugin</sub>
