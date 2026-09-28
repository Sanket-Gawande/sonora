# Android application

Kotlin + Jetpack Compose receiver, styled after the Windows notch (Mona Sans bundled in `res/font`, graphite surfaces, periwinkle accent).

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'
.\gradlew.bat assembleDebug   # app\build\outputs\apk\debug\app-debug.apk
```

Pinned toolchain: AGP 8.13.2, Gradle 8.13, Kotlin 1.9.22, Compose compiler 1.5.10, Compose BOM 2024.02.00, JDK 17+ (Android Studio's JBR works).

## Screens

- **Home**: the connected PC with what's playing and a play/pause button (tap the card to open the player); or the PC found over the cable with a Connect button; or, with no PC yet, three steps the phone checks off itself (USB debugging on, plugged into a computer, Sonora open on the PC).
- **Confirm pairing**: the number the PC and phone must agree on, for offers the phone didn't ask for.
- **Player** (`ui/Player.kt`), arranged like YouTube Music's: the cover under the header, the title and "artist · app" left-aligned under it (one line each; a long title scrolls), then the seek bar, one row of controls (output · previous · play/pause · next · disconnect) and the stream's live waveform (flat in silence), all on the cover's own colour, which the status bar shows too (the app draws edge to edge). The controls act on the PC's playing app. The output button opens the "Play on" sheet (Automatic, the phone speaker, wired, USB or Bluetooth headphones); the output shown is the one Android actually routes to. Latency sits top-right, measured end to end and coloured (green ≤ 60 ms, amber ≤ 120 ms, red above). ⌄ closes the player; the stream keeps playing. Volume is the phone's own buttons: the stream plays at full level. Every row has a fixed height and the cover takes the rest, so nothing moves while a track loads, and the last track stays up for 3 s when the PC reports nothing between songs.

`ReceiverModel` holds the UI state. The stream runs in `ReceiverService`, so leaving the player (Back) keeps it playing; only Disconnect ends it.

## Audio

- `net/PacketCodec.kt`: opens the packets the Windows sender seals (`docs/protocol.md`).
- `net/JitterBuffer.kt`: reorders, conceals single losses with a fade, drops late packets, trims when the sender runs ahead.
- `net/AudioReceiver.kt`: UDP (Wi‑Fi) or TCP over `adb reverse` (USB) → codec → jitter buffer → low-latency `AudioTrack`, on the output picked in the player (`setPreferredDevice`).
- `net/PcFinder.kt`: finds Sonora on the PC over the cable (`127.0.0.1:47211`), asks it to connect, ends the stream on the PC too, and, while this phone holds the stream, receives what's playing and sends the media buttons (`docs/protocol.md`).
- `net/Presence.kt`: that channel's wire format and proofs, free of Android so `PresenceTest` checks it against the Windows vector.
- `Outputs.kt`: the phone's media outputs and their names.
- `net/ClockSync.kt`: this phone's clock beside the PC's (PING/PONG) and the PC's clock sample, which turn the track's presentation timestamps into a latency (`docs/protocol.md`, "Latency").
- `ReceiverService.kt`: the foreground playback service. While a USB stream runs it holds a media session, so a Bluetooth or wired headset's play/pause, next and previous (a wired headset's double-press too, and a car's controls) reach the PC's playing app, and headsets and cars that show the track see the PC's; `MainActivity` takes the PC's pairing link from the `sonora_link` extra and shows the real number to confirm.

The player shows only measured values, and redraws only what changed: the position twice a second while playing, the latency once a second, and the waveform (20 fps, the bars alone).

`.\gradlew.bat testDebugUnitTest` runs the codec tests against Windows' `vector.txt`, plus the jitter-buffer tests.

## Next

Opus decoding with `MediaCodec`, NSD discovery on Wi‑Fi, and QR pairing with CameraX.
