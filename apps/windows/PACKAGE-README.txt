SONORA 0.4 - PREVIEW
Play your Windows PC's audio on your Android phone over a USB cable.

SET UP (ONCE)
1. Extract this folder anywhere and keep Sonora.exe in it.
2. Sonora talks to the phone through adb, part of Google's Android platform-tools:
   https://developer.android.com/tools/releases/platform-tools
   Download the Windows zip and extract its "platform-tools" folder next to Sonora.exe.
   (Android Studio already installed? Then there's nothing to do.)
3. On the phone, install Sonora-Android-0.4.apk, then turn on USB debugging:
   Settings > About phone > tap Build number 7 times, then
   Settings > System > Developer options > USB debugging.
4. Plug the phone in and tap Allow when it asks about USB debugging.

USE
Run Sonora.exe, then open Sonora on the phone: your PC shows up there. Tap Connect.
Sonora adds itself to the Start menu the first time it runs, so after a restart just search
"Sonora". To have it start by itself, turn on "Start with Windows" in its settings.
(Or open the notch on the PC and choose Connect over USB, then confirm the number on the phone.)

The phone's player shows what's playing on the PC, with previous, play/pause, next and seek.
Your earphones' buttons control it too. Pick the phone speaker, wired or Bluetooth
headphones from the output button. Volume is the phone's own volume buttons.
Latency is measured end to end and shown in colour; nothing is estimated.

ON THE PC
Sonora sits on the top edge of the screen. Click it, or press Ctrl+Alt+S from any app, to open it.
Escape or a click anywhere else closes it. It steps aside for fullscreen apps.
The notification-area icon has connect, disconnect, settings, hide and quit.
Settings (the gear) choose the display, fullscreen hiding, start with Windows, reduced motion
and whether the notch takes its colour from your wallpaper.

Audio is encrypted (AES-256 with HMAC-SHA256); the key only travels over the USB cable.
Preferences: %LOCALAPPDATA%\Sonora\preferences.xml. Errors: %LOCALAPPDATA%\Sonora\error.log.
Mona Sans is bundled inside the app; its license (SIL OFL 1.1) is included.
The app is unsigned: Windows may ask you to confirm the first run. This is a preview, not an installer.
To remove it: quit from the tray icon, turn off Start with Windows first if you enabled it, delete
this folder and the Sonora entry in the Start menu.
