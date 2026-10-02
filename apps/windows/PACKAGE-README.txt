SONORA 0.5 - PREVIEW
Play your Windows PC's audio on your Android phone, over Wi-Fi or a USB cable.

SET UP (ONCE)
1. Extract this folder anywhere and keep Sonora.exe in it.
2. On the phone, install Sonora-Android-0.5.apk.
3. Run Sonora.exe. Windows asks once whether Sonora may accept connections on your network:
   allow it, so the phone can find this PC on Wi-Fi.

USE ON WI-FI
Put the PC and the phone on the same Wi-Fi (or the PC on the phone's hotspot), open Sonora on
the phone and tap Connect over Wi-Fi. The first time, the phone and the notch show the same
number: click Allow on the PC if they match. After that it's one tap.

USE OVER USB (OPTIONAL)
Sonora talks to a phone on the cable through adb, part of Google's Android platform-tools:
   https://developer.android.com/tools/releases/platform-tools
Extract its "platform-tools" folder next to Sonora.exe (Android Studio installed? Nothing to do),
turn on USB debugging on the phone (Settings > About phone > tap Build number 7 times, then
Settings > System > Developer options > USB debugging), plug in and tap Allow.

ON THE PHONE
The player shows what's playing on the PC, with previous, play/pause, next and seek; your
earphones' buttons control it too. MUTE PC mutes the PC's speakers so only the phone plays
(they come back on when you disconnect). The source line (e.g. CHROME) with its arrow opens the
song on the phone. Volume is the phone's own buttons. Latency is measured end to end.

ON THE PC
Sonora sits on the top edge of the screen. Click it, or press Ctrl+Alt+S from any app, to open it.
Escape or a click anywhere else closes it. It steps aside for fullscreen apps.
The notch shows what's playing (click the song to jump to its app or browser tab), the PC's
volume and mute, and the phone. If something plays that nobody can hear (the PC and the phone
both muted), it says so in amber.
Sonora adds itself to the Start menu on first run. Settings (the gear): display, fullscreen
hiding, start with Windows, reduced motion, wallpaper colour, and phones on Wi-Fi (allow them,
or forget the ones you allowed).

Audio is encrypted (AES-256 with HMAC-SHA256). On Wi-Fi a phone pairs once with a number you
confirm on the PC; the keys are derived on both sides and never sent.
Preferences: %LOCALAPPDATA%\Sonora\preferences.xml. Errors: %LOCALAPPDATA%\Sonora\error.log.
Mona Sans is bundled inside the app; its license (SIL OFL 1.1) is included.
The app is unsigned: Windows may ask you to confirm the first run. This is a preview, not an installer.
To remove it: quit from the tray icon, turn off Start with Windows first if you enabled it, delete
this folder and the Sonora entry in the Start menu.
