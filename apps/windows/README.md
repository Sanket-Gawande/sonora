# Windows application

WPF on .NET Framework 4.8, built with the C# compiler that ships with Windows, so no SDK, NuGet or network access is needed. The compiler only supports C# 5 (no `$"…"`, `?.`, `nameof`, expression-bodied members); keep the source to that.

```powershell
.\build.ps1           # build\Sonora\Sonora.exe and a zip
.\build.ps1 -Verify   # also drive every state and write PNG snapshots to build\verification
.\tools\make-icon.ps1 # only when the mark changes; Assets\Sonora.ico is committed
.\build\Sonora\Sonora.exe --capture-test <dir> [--tone]   # 3 s of loopback capture, measured report
.\build\Sonora\Sonora.exe --transport-test <dir>          # packet crypto + real sender over 127.0.0.1; writes vector.txt
.\build\Sonora\Sonora.exe --usb-stream <dir> [seconds]    # pair with the USB phone and stream real audio, then report
.\build\Sonora\Sonora.exe --presence-test <dir>          # the phone channel's rules against a pretend phone; writes presence-vector.txt
```

`vector.txt` from the transport test is copied to `apps/android/app/src/test/resources/` so the Kotlin codec is checked against the Windows one.

## Layout

- `src/Program.cs`: entry point, single instance (a second launch opens the running notch), tray wiring.
- `src/NotchWindow.cs`: a transparent, click-through host pinned to the top edge of one display. It handles placement in physical pixels, the spring animation, the no-focus-steal window style, the Ctrl+Alt+S hotkey and fullscreen hiding.
- `src/NotchViews.cs`: idle, compact (now playing or the phone link), home (now playing with controls, then the phone), pairing and settings.
- `src/MediaSession.cs`: what Windows says is playing (title, artist, app, artwork, position) and the playing app's own previous, play/pause, next and seek, via the WinRT media-session API (`Windows.Media.Control`). Reads are never cancelled (a change mid-read queues one more), every call to Windows has a time-out, the artwork is re-read on every metadata update and compared by its bytes (browsers send a placeholder first), and a check every 5 s covers events Windows drops. `--media-test <dir>` prints what it sees; `--track-change` skips a track and checks the details that follow match a fresh read.
- `src/Session.cs`: the USB connection: find the phone, pair, stream, volume, mute, disconnect. The stream always carries whatever the PC plays. It also takes Connect and Disconnect from the phone, and restores the stream's tunnel when the cable comes back.
- `src/PhoneWatcher.cs`: phones as they're plugged in (adb's device feed, no polling), each given a presence tunnel on 47211 so the Sonora app can see this PC and connect from the phone, and, with the stream's key, see what's playing and use its buttons (`docs/protocol.md`, "Finding the PC over USB"). `--presence-test <dir>` checks the channel's rules against a pretend phone.
- `src/WallpaperTheme.cs`: the wallpaper's dominant colour, used to tone the notch background (Settings › Tint to wallpaper).

## Performance

- The window's height follows the notch (grown before it grows, shrunk after it shrinks; anchored at the top), not a fixed 900×375 transparent area, and it's rendered in software: no GPU read-back of a layered window each frame, and no blur effect. Its width stays at the widest the notch gets: a layered window that moves while it resizes shows its old picture shifted for a frame, which flickered. Views crossfade rather than cut, and the hover lean waits for a resize to finish.
- Only foreground changes are hooked; the old system-wide location-change hook fired for every window move (thousands per virtual-desktop switch). A 1.5 s check covers in-place fullscreen.
- The level meter runs at 20 fps, only in the compact view, animates with render transforms (no layout), and stops redrawing in silence. The wallpaper is decoded off the UI thread; the window only moves when its rectangle changes.
- `src/AudioMeter.cs`: the real peak level of the default output device, which drives the waveform.
- `src/Ui.cs`, `Styles.xaml`: tokens and controls from the canvas design (Mona Sans, a periwinkle accent, and a background toned from the wallpaper).
- `src/OutsideClicks.cs`: tells the open notch about clicks elsewhere, so it closes like a menu.
- `src/Tray.cs`, `src/Preferences.cs`, `src/Native.cs`, `src/Verifier.cs`.

## Behaviour

- Idle is a 132×28 lip on the top edge; click it or press Ctrl+Alt+S to open. Escape closes.
- Clicking never takes focus from the app you are in. The hotkey does, and gives it back on close.
- An open notch closes when you click anywhere else, like a menu (a mouse hook that exists only while it's open, on its own thread), or 2.5 s after the pointer leaves, except while pairing.
- It slides away when a fullscreen app covers its own display, and returns afterwards. The hotkey, the tray icon or a second launch still opens it over a fullscreen app; it slides away again when closed.
- Preferences: `%LOCALAPPDATA%\Sonora\preferences.xml`. Errors: `%LOCALAPPDATA%\Sonora\error.log`.

## Audio

- `src/LoopbackCapture.cs`: WASAPI shared-mode loopback of the default output, with silence fill while the device is idle.
- `src/PacketCodec.cs`: the sealed packet format in `docs/protocol.md` (AES-256-CTR + HMAC-SHA256, replay window).
- `src/Resampler.cs`: maps any mix format to 48 kHz stereo (linear interpolation for now).
- `src/AudioSender.cs`: capture → resample → 5 ms PCM16 packets → UDP. Keeps a clock sample (which stream frame Windows mixed when, from WASAPI's own timestamps) that the phone's latency reading is measured against.

- `src/UsbLink.cs`, `src/PacketSinks.cs`: finds the phone with adb (real phones before emulators), sets up `adb reverse`, hands over the pairing link, and serves packets over a loopback TCP listener. adb is looked for in a `platform-tools` folder next to `Sonora.exe` first (how the release is set up), then on PATH, then in the Android SDK.

In USB mode the volume slider and mute scale the stream itself. Every adb call has a timeout, so a wedged adb server can't freeze Sonora. Next: Wi‑Fi pairing by QR code, and now-playing on the phone.
