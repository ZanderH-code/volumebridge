**English** | [中文](README.zh-CN.md)

# VolumeBridge

**Is your JBL Flip 6 stuck at full volume when it is connected to a PC over USB? VolumeBridge makes the Windows volume slider and mute key control it again, with zero added latency and no Voicemeeter.**

> ✅ **Tested on a JBL Flip 6 in USB audio mode** (shown in Windows as "Speakers (Zgmicro AUDIO)") **on Windows 11**: the volume slider, volume keys and mute key all work, with zero added latency.
> It should also work with **older JBL speakers** (Flip 5, Flip 4, the Charge series, etc.) as long as they connect to the PC as a USB audio device, and with any other speaker or USB DAC that ignores the Windows volume over USB. These have not been tested yet; reports are welcome.

## Does this sound like your problem?

- Your JBL Flip 6 is connected with a USB-C cable in USB audio (wired) mode, and **the Windows volume slider does nothing**: it always plays at maximum volume.
- Even at 1% or **muted**, the speaker is still loud.
- The speaker's own volume buttons don't work in USB mode either.
- Windows lists the device as **"Speakers (Zgmicro AUDIO)"** or "Generic USB Audio".
- The usual advice is to put **Voicemeeter** in between to apply the volume. It is heavy, adds latency, is fiddly to set up and takes the speaker exclusively.

**Why it happens:** in this USB audio mode (a vendor demo/service mode) the speaker's firmware locks its volume at maximum. The speaker tells Windows it controls volume in hardware, so Windows only sends it the volume value and doesn't lower the audio itself. The speaker then ignores that value, so the system volume has no effect at all.

VolumeBridge lowers the audio inside the Windows audio engine, before it reaches the speaker, according to the volume slider and mute state. It behaves as if the speaker supported volume control on its own.

## Features

- **Zero-latency mode (default):** a volume plugin sits in the speaker's own effect chain and processes audio in place, with **no added latency**. Lip sync stays right in videos and games.
- The Windows **volume slider, keyboard volume keys and mute key** all work. Volume changes use a 20 ms ramp, so there are no clicks.
- **Maximum volume cap:** -12 dB by default, so a full slider is not deafening. Adjustable in the window.
- Window plus **tray icon** (closing the window keeps it running), **start at logon**, and **one-click updates**.
- English / 简体中文 UI, chosen in the installer and switchable at any time.
- **Compatible mode (fallback):** an emulated USB virtual speaker that forwards to the real one. It leaves system audio settings alone but adds about 40 ms of latency.
- Uninstalling restores every system setting.
- Free and open source (MIT). No Voicemeeter, VB-CABLE or Equalizer APO needed.

## Install and use

1. Connect the speaker with a USB cable and switch it to USB audio mode. On the Flip 6, hold **Bluetooth + Play for about 10 seconds**; check your model's method. **Quit Voicemeeter** if it is running, because it takes the speaker exclusively.
2. Download `VolumeBridgeSetup.exe` from [Releases](https://github.com/ZanderH-code/volumebridge/releases/latest) and run it, keeping "**Enable the zero-latency volume plugin**" ticked. Windows audio pauses for about 2 seconds during setup.
3. Open VolumeBridge and click **Set as default output**.
4. Play something. When the window shows "**● Working (zero latency)**", it's active. Tick **Start at logon** and you're done.

## How it works

**Zero-latency mode (default)**

```
Apps ──▶ your USB speaker (default output)
           └ VolumeBridge volume plugin (APO) in its effect chain: follows the Windows slider and mute, in place, no buffering
```

- The plugin is an Audio Processing Object (endpoint effect, EFX). It runs inside the Windows audio engine (audiodg) and multiplies the audio by slider × volume cap, with a 20 ms ramp.
- Volume source: Windows 11 APO volume notifications, with the tray app passing the slider value through shared memory as a fallback.
- It coexists with the speaker's existing "Microsoft Audio Home Theater Effects".

**Compatible mode (switch in the window)**

```
Apps ──▶ "VolumeBridge Speaker" (USB speaker emulated by VolumeBridge) ──▶ gain from Windows ──▶ your USB speaker
```

- Emulates a standard USB speaker over USB/IP using the [usbip-win2](https://github.com/vadimgrn/usbip-win2) driver (optional at install). It leaves system audio settings alone but adds about 40–60 ms of latency.

## Trade-offs and notes

- The plugin is not signed by Microsoft. To let Windows load it, the protected audio path is turned off (`DisableProtectedAudioDG=1`), as Equalizer APO also does. A few DRM players (for example the audio of some 4K streams) may be affected.
- The speaker's "**Audio enhancements**" setting must stay on "Device Default Effects" (Settings → System → Sound → the speaker). If it is set to Off, Windows skips the plugin.
- Apps playing in **exclusive mode** bypass the plugin.
- Moving the speaker to another USB port or a Windows feature update may drop the plugin setting. The window tells you, and **Enable plugin** fixes it.
- **If sound stops after installing:** uninstall VolumeBridge in Settings → Apps (this restores the original settings), or set the speaker's "Audio enhancements" to Off.

## Compatibility

| Device | Status |
| --- | --- |
| JBL Flip 6 (USB audio mode, "Speakers (Zgmicro AUDIO)") | ✅ Tested (Windows 11 26H2) |
| Older JBL speakers (Flip 5 / Flip 4, Charge, Pulse, Xtreme, …) | Should work if the speaker connects to the PC as a USB audio device. On some models the USB port only charges, and those cannot work. Not tested yet |
| Other speakers / USB DACs that ignore Windows volume over USB | Should work: pick the device under "Output device". Not tested yet |

- Requirements: Windows 10 1903+ or Windows 11, x64. Installing needs administrator rights; the app runs unelevated.
- By default, a playback device whose name contains `JBL` or `Zgmicro` is picked automatically. You can choose another one in the window.
- Please report whether your model works in [Issues](https://github.com/ZanderH-code/volumebridge/issues).

## FAQ

**Why not Voicemeeter?**
Zero-latency mode adds no intermediate buffering (Voicemeeter needs a virtual cable plus a second output). VolumeBridge only handles volume, uses next to no resources in the background, doesn't take the speaker exclusively, and needs almost no setup.

**How is this different from Equalizer APO?**
It uses the same kind of plugin (an APO). But in Equalizer APO the volume (Preamp) is a fixed value in a config file and **does not follow the Windows volume slider**. VolumeBridge's plugin follows the slider and the mute key.

**Does VolumeBridge have to keep running?**
When Windows delivers volume notifications to the plugin, the plugin works on its own. Ticking "Start at logon" is still recommended as a fallback; the tray app uses next to no resources.

## Configuration

Config and logs live in `%LOCALAPPDATA%\VolumeBridge` (**Open log folder** in the window). The plugin's logs are in `C:\ProgramData\VolumeBridge`. Settings in `config.json`:

| Key | Default | Meaning |
| --- | --- | --- |
| `mode` | `apo` | `apo` zero-latency mode; `virtual` compatible mode |
| `maxOutputDb` | -12 | Output at 100 % slider (dB); 0 = the speaker's own full level |
| `outputKeywords` | `["JBL","Zgmicro"]` | Name keywords for automatic device selection |
| `outputDeviceId` | `null` | Fixed output device (set by the drop-down) |
| `language` | `auto` | `zh` / `en`; `auto` = installer choice |
| `targetBufferMs` / `outputBufferMs` | 20 / 10 | Compatible-mode buffering |
| `minDb` / `maxDb` / `volumeStepDb` | -60 / 0 / 0.25 | Compatible-mode volume range and step offered to Windows |
| `usbipPort` | 3240 | Compatible-mode local USB/IP port |

## Building

```
dotnet test tests/VolumeBridge.Tests
dotnet publish src/VolumeBridge -c Release -r win-x64 -o out
```

CI (`.github/workflows/build.yml`) builds the volume plugin (`apo/`, C++) and the Inno Setup installer on Windows, and publishes a release for every `v*` tag. Design notes and review history are in [docs/PLAN.md](docs/PLAN.md) (Chinese).

## Credits and licenses

- The compatible mode's UAC1 device and USB/IP server are adapted from [Virtual Cables](https://github.com/tarekwasfy01/Virtual-Cables) (BSD-2-Clause).
- [usbip-win2](https://github.com/vadimgrn/usbip-win2) (BSD-2-Clause) is optionally bundled, unmodified.
- [NAudio](https://github.com/naudio/NAudio) (MIT).
- The APO registration follows the public behaviour of [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (none of its GPL code is copied).
- Full third-party license texts are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). VolumeBridge is released under the [MIT License](LICENSE).

<sub>Keywords: JBL Flip 6 USB volume not working, JBL Flip 6 PC volume stuck at max, JBL Flip 6 USB-C audio too loud, Windows volume slider does nothing USB speaker, mute not working USB audio, Zgmicro AUDIO volume, JBL demo mode / service mode, Flip 5 / Flip 4 / Charge 5 USB audio volume, USB speaker fixed volume, hardware volume ignored, software volume control for USB DAC, Voicemeeter alternative, Equalizer APO follow system volume, volume APO, Windows 11 USB audio volume control</sub>
