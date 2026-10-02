# Sonora audio protocol, version 1 (draft)

Status: packet layer implemented on Windows (`apps/windows/src/PacketCodec.cs`) and Android (`apps/android/app/src/main/java/app/sonora/receiver/net/PacketCodec.kt`), cross-checked with shared test vectors. USB pairing, the presence channel, and Wi‑Fi discovery, pairing and streaming are built.

## Keys

Pairing gives both sides a random 32-byte session secret `K` (planned: carried in the QR code the PC shows, then confirmed with the six-digit number). Two keys are derived from it:

- `encKey = HMAC-SHA256(K, "sonora-v1-enc")`
- `macKey = HMAC-SHA256(K, "sonora-v1-mac")`

## Pairing link and number

The PC shows a QR code containing:

`sonora://pair?v=1&h=<host>&p=<udp port>&n=<pc name, URL-encoded>&k=<secret, base64url without padding>`

Both screens show a six-digit number derived from the secret: the first 4 bytes of `HMAC-SHA256(K, "sonora-v1-sas")` read as a big-endian unsigned integer, mod 1 000 000, shown as `ddd ddd`. The user checks that they match. Implemented in `Pairing.cs` and `PairingLink.kt` and cross-checked with `vector.txt`. Not yet built: rendering the QR code on Windows, scanning it on Android, and the control channel that confirms the match before audio starts.

## Audio packet (UDP)

| Offset | Size | Field |
|---|---|---|
| 0 | 2 | Magic `0x53 0x4E` ("SN") |
| 2 | 1 | Version `1` |
| 3 | 1 | Flags (bit 0: silence) |
| 4 | 4 | Stream id, random per stream (big-endian) |
| 8 | 4 | Sequence number, starts at 0, +1 per packet (big-endian) |
| 12 | 8 | Capture timestamp in frames since stream start (big-endian) |
| 20 | n | Ciphertext: interleaved PCM 16-bit little-endian, 48 kHz stereo |
| 20+n | 16 | Tag: first 16 bytes of `HMAC-SHA256(macKey, bytes[0 .. 20+n))` |

- Payload is 5 ms of audio (240 frames × 2 ch × 2 bytes = 960 bytes), so a packet is 996 bytes and never fragments on a 1500-byte MTU.
- Encryption is AES-256-CTR with `encKey`. The initial counter block is `streamId (4) ‖ sequence (4) ‖ 0x00 × 8`; the low 8 bytes count 16-byte blocks. Stream id and sequence never repeat together under one key, so no counter block is reused.
- Encrypt-then-MAC: the receiver checks the tag in constant time before decrypting.
- Replay: the receiver keeps the highest sequence seen and a 64-packet bitmap, and drops anything already seen or older than the window.

## USB transport

`adb reverse` tunnels TCP only, so over USB the phone opens a TCP connection to `127.0.0.1:47210`, which the tunnel carries to the PC's loopback listener. Each sealed packet is sent as `[u16 big-endian length][packet]`. Encryption, tags and the replay window are unchanged.

USB pairing: the PC generates the secret, runs `adb reverse tcp:47210 tcp:47210`, then opens `app.sonora.receiver/.MainActivity` with the pairing link as the `sonora_link` string extra (extras are not written to the system log). The phone shows the six-digit number; it connects only after the user confirms the match. The PC listens on loopback only, so nothing on the LAN can connect.

## Finding the PC over USB

Sonora on Windows follows adb's device list (`host:track-devices` on the adb server, one idle connection, no polling). For each phone in the `device` state it opens a loopback listener on a free port P and runs `adb -s <serial> reverse tcp:47211 tcp:P`, so the Sonora app on that phone reaches this PC at its own `127.0.0.1:47211` while the cable is in. The port a connection arrives on tells the PC which phone it is. Quitting removes the tunnels; a stale one reads as "no PC" (the connection opens, then closes without a greeting), and the phone backs off to one try every 8 s.

Lines are UTF-8, `\n`-terminated, `VERB key=value&key=value` with URL-encoded values; anything over 1 KB is dropped.

| From | Line | Meaning |
|---|---|---|
| Phone | `HELLO v=1&model=<model>` | First line, within 5 s. |
| PC | `SONORA v=1&name=<pc>&challenge=<32 hex>&state=…&you=0\|1[&busy=1][&problem=…]` | The greeting, with the state below. |
| PC | `STATE state=<offline\|pairing\|streaming\|waiting>&you=0\|1[&busy=1][&problem=…]` | Sent whenever it changes. `you`: the stream is with this phone. `busy`: it is with another phone. `problem`: why the PC's last attempt for this phone failed. |
| Phone | `CONNECT request=<32 hex>` | Start a stream to this phone (moving it from another phone if needed). |
| Phone | `END proof=<64 hex>` | Disconnect. `proof` = `HMAC-SHA256(K, "sonora-v1-end" ‖ challenge)`, so only the phone holding this stream's key can end it. |
| Phone | `AUTH proof=<64 hex>` | `HMAC-SHA256(K, "sonora-v1-auth" ‖ challenge)`: this connection holds the stream's key. Sent when the stream starts and after every reconnect. The PC checks it again whenever the key changes, so it lapses when the stream ends or moves. |
| PC | `ART id=<16 hex>&jpeg=<base64url>` | Artwork (a JPEG of at most 160 KB, up to 480 px on its longer side), to an authorised connection, once per picture per connection, just before the `MEDIA` line naming it. |
| PC | `MEDIA title=…&artist=…&app=…&playing=0\|1&toggle=0\|1&previous=0\|1&next=0\|1&seek=0\|1&position=<ms>&duration=<ms>[&art=<id>]` | What's playing, to an authorised connection, whenever it changes; `MEDIA none=1` when nothing is, or when the connection stops being authorised. The flags say which buttons the playing app accepts. `position` was true when the line was sent; the phone advances it while `playing=1`. |
| Phone | `CONTROL action=<toggle\|play\|pause\|previous\|next>` or `CONTROL action=seek&position=<ms>` | The playing app's own buttons (`play`/`pause` are explicit, for headphone buttons). Obeyed only from an authorised connection; anything else is dropped. |
| Phone | `CONTROL action=mute\|unmute` | The PC's speakers (Windows' own mute; the capture taps the mix before it, so the stream carries on). Muted this way, they come back on when that phone's stream ends. The stream's phone hears the state as `&pcmuted=1` on its `STATE` line. |
| Phone | `VOLUME level=<0-100>` | The phone's media volume, sent after `AUTH` and whenever it changes, so the PC can warn when something plays that nobody can hear (its speakers muted, and the phone muted or at 0). |
| Phone | `LINK request=<8 hex>` | Asks for a link to what's playing, to open it on the phone. Authorised connections only, one at a time. |
| PC | `LINK request=<echoed>&url=<link>` or `LINK request=<echoed>&none=1` | The playing browser tab's address when that tab is the one showing in its window (read from the browser's address bar; YouTube video links carry `t=` for the position), otherwise `none`. Only `http`/`https`. The phone opens it, or on `none` a search for the track (Spotify for Spotify, YouTube otherwise), then sends `CONTROL action=pause` so it plays in one place. |
| Phone | `PING t=<phone µs>` | Clock sync for the latency reading (below). Authorised connections only, at most 20 a second. |
| PC | `PONG t=<echoed>&time=<PC µs>[&frame=<F>&at=<PC µs>]` | `time` is read the moment the PING arrives. `frame`/`at`: stream frame F was mixed by Windows at `at`. |

On `CONNECT` the PC generates a new secret and opens the app over adb as in USB pairing, adding the request token as the `sonora_request` extra. The app starts without showing the number only when the token matches one it sent in the last 60 s; any other offer (the notch's Connect over USB, or an intent from another app) shows the number. The key never crosses the presence channel, so another app on the phone that connects to 47211 can see the PC's name and state, and ask it to connect, but cannot hear the stream or end it.

Nothing about media reaches, or is obeyed from, a connection without the stream's key: another app on the phone sees the PC's name and state only. `Sonora.exe --presence-test` plays a phone against the real channel and checks each of these rules; it also writes `presence-vector.txt`, which the Android tests use to check they compute the same proofs.

A phone streaming over USB that hears `you=0` after having heard `you=1` (the PC's Disconnect or Quit, or a PC restart that lost the key), or a `problem` while it waits, stops its stream.

## Wi‑Fi

The same presence channel runs over the LAN: the PC listens on TCP 47211 on every interface (Windows asks once whether to allow Sonora on private networks) and answers discovery on UDP 47212.

**Discovery.** While the app is on screen it broadcasts `SONORA? v=1` to UDP 47212 every 2 s (to the Wi‑Fi's own broadcast address and 255.255.255.255, on a socket bound to the Wi‑Fi network). Each PC answers the sender with `SONORA! v=1&name=<pc>&id=<16 hex>&port=<tcp port>`. `id` is the PC's own random ID, made once; the phone keys its pairings by it, and a PC reachable by cable and Wi‑Fi shows once (its USB greeting carries the same `id`).

**Channel.** The phone connects to the PC's TCP port and says `HELLO v=1&model=<model>&id=<16 hex>`; over Wi‑Fi the `id` (the app's own random ID) is required and names the phone. The greeting adds `&id=<pc id>`. Over Wi‑Fi `CONNECT` (USB's) is ignored; instead:

| From | Line | Meaning |
|---|---|---|
| Phone | `PAIR key=<base64url, 64 bytes>` | A fresh P-256 public key, X ‖ Y. At most three per connection. |
| PC | `PAIRING key=<base64url, 64 bytes>` | The PC's half. Both sides now hold the long-term key `L = HMAC-SHA256(SHA-256(Z), "sonora-v1-pair" ‖ phone key ‖ PC key)`, where Z is the ECDH shared secret, and show its number (the same derivation as a USB offer's, from `L`). The PC's notch asks to allow the phone, showing the number. |
| PC | `PAIRED ok=1\|0` | The user's answer (no answer within a minute is `ok=0`). On `ok=1` both sides keep `L`: the PC encrypted for the Windows user (DPAPI), the phone in its private storage. |
| Phone | `JOIN port=<udp port>&nonce=<32 hex>&proof=<64 hex>` | Start a stream. `proof = HMAC-SHA256(L, "sonora-v1-wifi" ‖ challenge ‖ nonce ‖ port)`, so it can't be replayed on another connection. |
| PC | `JOINED ok=1&proof=<64 hex>` or `JOINED ok=0` | `proof = HMAC-SHA256(L, "sonora-v1-joined" ‖ challenge ‖ nonce)`: the phone accepts the join only with it, so something else answering at the PC's address gets nowhere. `ok=0`: the PC doesn't know this phone (never paired, or forgotten in Settings), or the proof is wrong; the phone pairs again. On `ok=1` the stream's key is `K = HMAC-SHA256(L, "sonora-v1-stream" ‖ challenge ‖ nonce)` on both sides, and the PC sends audio packets as UDP datagrams to the phone's address and `port`. |

After a declined (or unanswered) pairing, that address can't ask again for 30 s, and the PC keeps at most 4 Wi‑Fi connections per address (16 in all), so nobody on the network can keep the notch popping up. Settings › Allow phones on Wi‑Fi turns the listener off entirely (USB only). The control lines themselves are not encrypted on the network: the audio is, and every key is derived, never sent.

From then on the channel works as over USB: `AUTH` with `K`, then `MEDIA`, `ART`, `CONTROL`, `PING`, `LINK` and `END`. Neither `L` nor `K` ever crosses the network. The phone pings every 2 s while it streams; a PC that hasn't heard from it for 20 s stops the stream. The phone's jitter buffer adapts: it starts at 40 ms over Wi‑Fi (25 ms over USB), grows by 10 ms each time playback runs dry (to at most 200 ms), sheds a burst's excess within a second (quiet packets first) and creeps back down to 20 ms (15 ms) while the connection is steady. Its AudioTrack keeps only 15 ms queued, more only after an actual underrun.

`Sonora.exe --presence-test` plays a Wi‑Fi phone too (discovery, a declined and an allowed pairing, a wrong and a right join, media after `AUTH`) and adds the pairing to `presence-vector.txt`, which `WifiTrustTest` checks the phone derives identically.

## Latency

The player shows one number: the time from Windows mixing a sound to this phone's speaker playing it, measured, never estimated.

- **PC clock sample.** WASAPI stamps each loopback buffer with the performance-counter time the engine mixed its first frame. The sender records, a few times a second, which stream frame (the packets' timeline: sequence × 240) that buffer starts at, and its time in µs.
- **Clock offset.** The phone sends `PING` every 2 s (five quickly after `AUTH`). For each `PONG`, offset = `time` − (sent + received) / 2 on the phone's `System.nanoTime` clock. The quickest of the last eight round trips wins, since the error is at most half the round trip (about 1 ms over USB).
- **Playout.** Twice a second the phone takes the track's own presentation timestamp (`AudioTrack.getTimestamp`) and works out when the first frame of the packet it's writing leaves the speaker.
- **Latency** = playout time + offset − (sample time + (frame − sample frame) / 48 kHz). The screen shows the median of the last five one-second readings, and "Measuring…" until there are two. Readings below zero or above 5 s are dropped, and nothing is shown for a stalled stream.

Colours: green up to 60 ms (video on the PC still looks in sync), amber up to 120 ms, red above. Bluetooth headphones may add delay the phone's timestamps don't include.

## Not in this layer

Opus compression, clock-drift correction and the jitter buffer are separate pieces.
