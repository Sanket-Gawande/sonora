<div align="center">

<img src="docs/icon.png" width="88" alt="Sonora icon">

# Sonora

**Your PC's sound, on your phone.**

Stream everything your Windows PC plays to an Android phone over a USB cable,<br>
with the song, cover art and controls on both screens.

[![Latest release](https://img.shields.io/github/v/release/Sanket-Gawande/sonora?label=release&color=a3b8ff&labelColor=17171a)](https://github.com/Sanket-Gawande/sonora/releases/latest)
[![Windows 10 and 11](https://img.shields.io/badge/Windows-10%20%7C%2011-a3b8ff?labelColor=17171a)](#get-started)
[![Android 8.0 and later](https://img.shields.io/badge/Android-8.0%2B-a3b8ff?labelColor=17171a)](#get-started)
[![Preview](https://img.shields.io/badge/status-preview-f0b35a?labelColor=17171a)](#progress)

[**Download**](https://github.com/Sanket-Gawande/sonora/releases/latest) &nbsp;·&nbsp; [Get started](#get-started) &nbsp;·&nbsp; [How it works](#how-it-works) &nbsp;·&nbsp; [Roadmap](#coming-next)

<br>

<img src="docs/screenshots/windows-desktop-open.jpg" width="100%" alt="A Windows desktop with the Sonora notch open at the top edge: the playing track with its controls, and a phone streaming over USB">

<sub>The notch on Windows, open: what's playing, and the phone it's streaming to.</sub>

<br><br>

<img src="docs/screenshots/android-player.png" width="240" alt="The Android player: cover art, title, seek bar, controls and the playing app">
&nbsp;&nbsp;
<img src="docs/screenshots/android-home.png" width="240" alt="The Android home screen: connected to the PC, streaming, with a mini player">
&nbsp;&nbsp;
<img src="docs/screenshots/android-output.png" width="240" alt="Choosing where the phone plays: Automatic or the phone speaker">

<sub>On the phone: the player, home, and the output picker.</sub>

</div>

## Why Sonora

<table>
<tr>
<td width="50%" valign="top">

**🔌 One tap to connect**<br>
Plug the phone in and your PC shows up in the app. Tap Connect. No account, no cloud, no Wi‑Fi setup.

</td>
<td width="50%" valign="top">

**🎵 A real music player**<br>
Title, artist, cover and position from whatever plays on the PC (Spotify, YouTube in a browser, Media Player), with previous, play/pause, next and seek.

</td>
</tr>
<tr>
<td valign="top">

**🎧 Your earphones work**<br>
Play/pause and skip on wired or Bluetooth earphones control the PC. Pick the phone speaker, wired, USB or Bluetooth output.

</td>
<td valign="top">

**⏱️ Latency, measured**<br>
The delay from your PC to the phone's speaker is measured end to end and colour-coded. Nothing is estimated.

</td>
</tr>
<tr>
<td valign="top">

**🔒 Private by design**<br>
Audio is sealed with AES‑256 and HMAC‑SHA256. The key only travels over the cable, and other apps on the phone can't listen in or take control.

</td>
<td valign="top">

**🪟 A notch that stays out of the way**<br>
It opens with a click or <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>S</kbd>, never steals focus, closes when you click away, steps aside for fullscreen apps and takes its colour from your wallpaper.

</td>
</tr>
</table>

<div align="center">
<img src="docs/screenshots/windows-desktop-compact.jpg" width="100%" alt="The same desktop with the notch closed: a slim bar showing the track, the phone and a live level">
<sub>Closed, the notch is a slim bar with the track, the phone and a live level.</sub>
</div>

## Get started

| | Download | You need |
|---|---|---|
| **Windows** | [`Sonora-Windows-0.4.zip`](https://github.com/Sanket-Gawande/sonora/releases/download/v0.4.0/Sonora-Windows-0.4.zip) | Windows 10 or 11, and Google's [platform-tools](https://developer.android.com/tools/releases/platform-tools) |
| **Android** | [`Sonora-Android-0.4.apk`](https://github.com/Sanket-Gawande/sonora/releases/download/v0.4.0/Sonora-Android-0.4.apk) | Android 8.0 or later, a USB cable, USB debugging on |

1. **Windows:** extract the zip anywhere, then extract Google's `platform-tools` folder next to `Sonora.exe`. Already have Android Studio? Skip that part.
2. **Android:** install the APK, then turn on **USB debugging**: *Settings › About phone*, tap *Build number* 7 times, then *Settings › System › Developer options › USB debugging*.
3. **Plug the phone in** and tap **Allow** when it asks about USB debugging.
4. **Run `Sonora.exe`**, open Sonora on the phone and tap **Connect**. That's it.

Sonora adds itself to the Start menu on first run, so after a restart it's one search away. Want it running all the time? Turn on **Start with Windows** in its settings.

> [!NOTE]
> These are preview builds. The Windows app isn't signed yet, so Windows may ask you to confirm the first run, and the APK is installed by sideloading.

## How it works

```mermaid
flowchart LR
    subgraph pc [Windows PC]
        app[Anything playing] --> capture[Loopback capture<br/>48 kHz stereo]
        capture --> seal[5 ms packets<br/>AES-256 + HMAC]
        media[Windows media session<br/>track, cover, controls]
    end
    subgraph phone [Android phone]
        buffer[Jitter buffer] --> out[Speaker, wired<br/>or Bluetooth]
        player[Player and<br/>earphone buttons]
    end
    seal == USB cable ==> buffer
    media -. now playing .-> player
    player -. play, pause, skip, seek .-> media
```

Sonora captures the PC's output as it's mixed, cuts it into 5 ms encrypted packets and sends them over the cable with `adb reverse`. The phone reorders them, fills any gap and plays them. A second channel on the same cable lets the phone find the PC, connect, show what's playing, send media commands and sync clocks to measure latency. The full protocol is in [docs/protocol.md](docs/protocol.md).

## Progress

| | Status |
|---|---|
| USB streaming, Windows → Android | ✅ Working |
| Phone finds the PC and connects in one tap | ✅ Working |
| Now playing on the phone, with controls and earphone buttons | ✅ Working |
| Measured end-to-end latency | ✅ Working |
| Output picker: speaker, wired, USB, Bluetooth | ✅ Working |
| Windows notch: hotkey, tray, Start menu, fullscreen-aware, wallpaper colour | ✅ Working |
| Wi‑Fi streaming | 🗓️ Planned |

## Coming next

- **Wi‑Fi streaming**, paired by scanning a QR code.
- **Lower latency**: a buffer that shrinks while the connection is steady, and Android's fast audio path.
- **Lock-screen and notification** media controls on the phone.
- **Signed builds** and a Windows installer.

## Build from source

<details>
<summary><b>Windows</b>: no SDK needed, it uses the C# compiler that ships with Windows</summary>

```powershell
cd apps\windows
.\build.ps1 -Verify        # build\Sonora\Sonora.exe, a zip, and a self-check with snapshots
```

</details>

<details>
<summary><b>Android</b>: JDK 17 (Android Studio's bundled one works)</summary>

```powershell
cd apps\android
.\gradlew.bat assembleDebug testDebugUnitTest
```

</details>

Each app's README ([Windows](apps/windows/README.md), [Android](apps/android/README.md)) covers its layout and self-tests.

```
apps/windows/   the Windows notch (WPF, .NET Framework 4.8)
apps/android/   the Android player (Kotlin, Jetpack Compose)
docs/           protocol and product notes, screenshots
design/         the original concept
```

<sub>Sonora uses the <a href="https://github.com/github/mona-sans">Mona Sans</a> typeface (SIL Open Font License 1.1). The demo track in the screenshots, "Afterglow", and its cover were made for this project.</sub>
