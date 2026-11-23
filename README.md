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

**Requirements:** Windows 10 (1903+) or Windows 11, 64-bit (x64). Portable single executable — no installer needed. One administrator (UAC) prompt is needed to enable or remove the plugin; the app itself runs without elevation.

> **Upgrading from 0.4.0 or earlier:** Uninstall the old installed version in **Settings → Apps** first, then run the new `VolumeBridge.exe`.

1. **Switch your speaker to USB audio mode** and connect it to your PC. On the JBL Flip 6, hold **Bluetooth + Play for about 10 seconds** (check your model's instructions if using another speaker). **Quit Voicemeeter** if it is running so it does not hold the speaker exclusively.
2. **Download and run `VolumeBridge.exe`** from the [latest release](https://github.com/ZanderH-code/volumebridge/releases/latest) (place it anywhere you like). Because the executable is unsigned, Windows SmartScreen may show a warning — click **More info → Run anyway**.
3. **Click Enable plugin** (one UAC prompt). Windows audio will pause for about 2 seconds while the plugin is registered.
4. **Click Set as default output**, play audio to confirm the window shows **● Working (zero latency)**, then tick **Start at logon**.

### Everyday controls

- **Max volume cap:** Defaults to **-12 dB** so 100% on the Windows slider is not deafening. Adjustable in the window from **0 to -30 dB**.
- **Output device picker:** Automatically picks a playback device whose name contains `JBL` or `Zgmicro`, or you can select another device manually.
- **Window and tray:** Closing the window minimizes VolumeBridge to the system tray so it stays out of the way.
- **Language:** Follows your Windows display language by default, and switches between English and 简体中文 anytime in the app.
- **Updates:** One-click update check from GitHub Releases (downloads are SHA-256 verified and update the exe in place).
- **Clean uninstall:** Click **Remove plugin** in the window (restores all original Windows audio settings and removes the logon task), then quit and delete `VolumeBridge.exe`.

## Trade-offs and troubleshooting

Zero-latency mode modifies one Windows audio setting so the plugin can load. Know these trade-offs before enabling it:

- **Protected audio path is turned off (`DisableProtectedAudioDG=1`):** The plugin is not signed by Microsoft, so Windows needs this setting to load it (Equalizer APO does the same). A few DRM players (such as certain protected 4K audio streams) may be affected.
- **Keep Audio enhancements on:** In **Settings → System → Sound → [your speaker]**, **Audio enhancements** must stay set to **Device Default Effects**. Turning it off makes Windows skip the plugin.
- **Exclusive mode bypasses the plugin:** Apps that take exclusive WASAPI control bypass the Windows audio engine and will not be attenuated.
- **USB port changes or Windows feature updates:** Moving the speaker to a different USB port or installing a major Windows update can drop the plugin setting. The VolumeBridge window will tell you, and clicking **Enable plugin** restores it.
- **If sound stops working after enabling:** Click **Remove plugin** in VolumeBridge (which restores everything), or set the speaker's **Audio enhancements** to **Off**.

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

```text
Apps ──▶ Your USB speaker (default output)
           └ VolumeBridge volume plugin (EFX APO in audiodg): applies slider × max-volume cap in place (20 ms ramp)
```

- Runs as an unsigned endpoint-effect Audio Processing Object (EFX APO) inside the Windows audio engine (`audiodg`), so audio is attenuated in place with no extra buffering.
- Reads volume from Windows 11 APO volume notifications; the tray app also passes the slider state via shared memory as a fallback.
- Coexists with the speaker's existing **Microsoft Audio Home Theater Effects**.
- Turning the protected audio path off (`DisableProtectedAudioDG=1`) and the speaker's effect settings are restored on uninstall.

## Configuration and logs

- **Config and app logs:** `%LOCALAPPDATA%\VolumeBridge` (click **Open log folder** in the window).
- **Plugin logs:** `C:\ProgramData\VolumeBridge`.

Settings in `%LOCALAPPDATA%\VolumeBridge\config.json`:

| Key | Default | Description |
| --- | --- | --- |
| `maxOutputDb` | `-12` | Output level at 100% slider (dB); `0` = speaker's full level |
| `outputKeywords` | `["JBL", "Zgmicro"]` | Case-insensitive name keywords for automatic device selection |
| `outputDeviceId` | `null` | Fixed output endpoint ID (set by the device picker) |
| `language` | `"auto"` | `"en"`, `"zh"`, or `"auto"` (follows Windows display language) |

## Building from source

```powershell
# 1. Build the C++ APO DLL (from a Visual Studio x64 Developer Prompt)
./apo/build.ps1 -Out "$PWD\out"

# 2. Run tests and build the portable single exe (.NET Framework 4.8)
dotnet test tests/VolumeBridge.Tests
dotnet build src/VolumeBridge -c Release "-p:ApoDll=$PWD\out\VolumeBridgeApo.dll" -p:RequireApo=true -o dist
```

The GitHub Actions workflow (`.github/workflows/build.yml`) builds the C++ APO (`apo/`) and embeds it into the single portable `VolumeBridge.exe` on Windows, and publishes a GitHub Release on every `v*` tag. Design notes and history are in [docs/PLAN.md](docs/PLAN.md) (Chinese).

## Credits and licenses

- [NAudio](https://github.com/naudio/NAudio) (MIT).
- APO registration follows the public behaviour of [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (no GPL code is copied).
- Full third-party notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). VolumeBridge is licensed under the [MIT License](LICENSE).

<sub>Keywords: JBL Flip 6 USB volume not working, JBL Flip 6 PC volume stuck at max, JBL Flip 6 USB-C audio too loud, Windows 11 USB speaker volume slider does nothing, mute not working USB audio, Speakers Zgmicro AUDIO, Generic USB Audio fixed volume, JBL demo mode service mode, Flip 5 Flip 4 Charge USB audio volume, USB DAC fixed volume Windows 11, Voicemeeter alternative, Equalizer APO follow Windows volume slider, volume APO plugin</sub>
