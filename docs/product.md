# Product direction

## Initial scope

Windows sends desktop audio to one Android phone on the same local network. Original design inspired by compact notch utilities: soft continuous corners, dark surfaces, restrained accents, clear typography, and responsive expansion.

## Interaction contract

- Disconnected: show a clear connect action; never display a fake connected status.
- Pairing: identify the computer and phone and require approval before streaming.
- Connected, compact: device name and unobtrusive activity indicator.
- Connected, expanded: source, volume, mute, pause, and disconnect.
- Interrupted: show reconnect progress and a cancel action; keep controls understandable.
- Keyboard: visible focus, Enter/Space activation, Escape to collapse.
- Accessibility: reduced motion and sufficient contrast; do not communicate status only through color.

## Engineering checkpoints

Establish an end-to-end audio proof early. Measure actual latency rather than promising the concept image's 18 ms. Bluetooth output adds its own delay. Pause playback and disconnect must have distinct semantics. Do not ship unauthenticated audio streaming.

The native island must not steal focus, intercept clicks outside its visible bounds, or cover fullscreen content unexpectedly. Evaluate per-monitor DPI, multiple monitors, and sleep/resume.

## Prototype boundaries

`design/island.html` is the original dependency-free UX study, with simulated state and no device access. The apps in `apps/` now implement the real thing over USB: capture, encryption, discovery, pairing, playback and media controls. Wi‑Fi streaming is still to come.
