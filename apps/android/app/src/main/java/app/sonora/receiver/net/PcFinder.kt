package app.sonora.receiver.net

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Network
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Log
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import java.io.IOException
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.net.URLEncoder
import java.security.SecureRandom
import java.util.Base64
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

// The channel to Sonora on a PC. Normally the PC this phone is plugged into: Sonora on Windows
// watches for phones with USB debugging on and runs `adb reverse tcp:47211` for each, so while the
// cable is in, the PC answers on this phone's own loopback. For a PC on Wi‑Fi (`useWifi`) the same
// channel runs to that PC's address instead, and adds pairing and joining. Through it the phone
// asks the PC to connect and ends the stream, and, while it holds the stream, sees what's playing
// on the PC and controls it. docs/protocol.md, "Finding the PC over USB" and "Wi‑Fi".
object PcFinder {
    const val PORT = 47_211
    const val LOOPBACK = "127.0.0.1"

    // What the PC says. `mine`: its stream is with this phone. `busy`: it's streaming to another
    // phone. `problem`: why the PC's last attempt to connect this phone failed. `id`: the PC's own
    // ID, the same over USB and Wi‑Fi.
    // `pcMuted`: the PC's speakers are muted (told only to the phone holding its stream).
    data class Pc(val name: String, val state: String, val mine: Boolean, val busy: Boolean, val problem: String?, val id: String? = null, val host: String = LOOPBACK, val pcMuted: Boolean = false)

    // This phone's ID (Trust), sent in HELLO; a PC on Wi‑Fi knows its paired phones by it.
    @Volatile var phoneId: String? = null
    // This phone's media volume, 0..100 (the service sets it), sent to the PC while streaming so it
    // can tell when nothing can hear what's playing.
    @Volatile var mediaVolume: (() -> Int)? = null
    @Volatile private var sentVolume = -1

    // Main thread only.
    var pc by mutableStateOf<Pc?>(null)
        private set
    // What's playing on the PC, and its artwork; only while this phone holds the PC's stream.
    var media by mutableStateOf<Presence.Media?>(null)
        private set
    var artwork by mutableStateOf<Bitmap?>(null)
        private set
    // This phone's clock beside the PC's, for the latency reading.
    val clock = ClockSync()

    private val main = Handler(Looper.getMainLooper())
    private val writer = Executors.newSingleThreadExecutor()
    private val listeners = CopyOnWriteArrayList<(Pc) -> Unit>()
    private val mediaListeners = CopyOnWriteArrayList<() -> Unit>()
    private val holders = mutableSetOf<Any>()
    private val lock = Object()
    @Volatile private var wanted = false
    @Volatile private var socket: Socket? = null
    @Volatile private var output: OutputStream? = null
    @Volatile private var challenge: String? = null
    @Volatile private var authSecret: ByteArray? = null
    private var worker: Thread? = null
    private var request: String? = null
    private var requestedAt = 0L
    // Where the channel runs: this phone's loopback (USB), or a PC's address on the Wi‑Fi network.
    @Volatile private var host = LOOPBACK
    @Volatile private var network: Network? = null
    val overWifi: Boolean get() = host != LOOPBACK
    // The host the open connection greeted us from: pairing and joining only go to the PC asked for.
    @Volatile private var connectedHost: String? = null
    private val onWifiPc: Boolean get() = overWifi && output != null && connectedHost == host
    // Wi‑Fi pairing and joining, waiting for the PC (callbacks on the main thread).
    @Volatile private var pairing: WifiTrust.Pairing? = null
    @Volatile private var agreed: ByteArray? = null
    private var onCode: ((String) -> Unit)? = null
    private var onPaired: ((ByteArray?) -> Unit)? = null
    private var onJoined: ((Boolean) -> Unit)? = null
    // The proof a genuine JOINED carries for the join waiting on it.
    @Volatile private var joinedProof: String? = null
    // The LINK request waiting for its answer (main thread only).
    private var linkRequest: String? = null
    private var linkDone: ((String?) -> Unit)? = null
    // Clock sync: a quick burst after AUTH, then one PING every 2 s while this phone holds the stream.
    @Volatile private var burst = 0
    @Volatile private var pingedAt = 0L

    init {
        Executors.newSingleThreadScheduledExecutor { Thread(it, "Sonora clock").apply { isDaemon = true } }
            .scheduleWithFixedDelay({ pingIfDue() }, 250, 250, TimeUnit.MILLISECONDS)
    }

    // The finder runs while anything holds it: the visible app, or a USB stream in the service.
    fun hold(holder: Any) {
        holders += holder
        synchronized(lock) { wanted = true; lock.notifyAll() }
        if (worker == null) worker = thread(name = "Sonora PC finder", isDaemon = true) { loop() }
    }

    fun release(holder: Any) {
        holders -= holder
        if (holders.isNotEmpty()) return
        synchronized(lock) { wanted = false }
        // Through the writer, so a Disconnect sent just before still reaches the PC.
        writer.execute { if (!wanted) socket?.let { runCatching { it.close() } } }
    }

    // Moves the channel to a PC on Wi‑Fi (its address, on the Wi‑Fi `network`), or back to USB with
    // null. The current connection closes and the next one goes there.
    fun useWifi(address: String?, network: Network?) {
        val next = address ?: LOOPBACK
        if (next == host) return
        host = next
        this.network = if (address == null) null else network
        main.post { pc = null }
        writer.execute { socket?.let { runCatching { it.close() } } }
        synchronized(lock) { lock.notifyAll() }
    }

    // Main thread. Pairs with the PC on Wi‑Fi: `code` gets the number to show once the PC sends
    // its half of the key agreement; `done` gets the long-term key once the user allows this phone
    // on the PC, or null (declined, or the connection dropped).
    fun pair(code: (String) -> Unit, done: (ByteArray?) -> Unit): Boolean {
        if (!onWifiPc) return false
        val mine = WifiTrust.pairing()
        pairing = mine
        agreed = null
        onCode = code
        onPaired = done
        send("PAIR key=" + WifiTrust.base64Url(mine.public))
        return true
    }

    private fun finishPair(key: ByteArray?) {
        val done = onPaired
        pairing = null; agreed = null; onCode = null; onPaired = null
        done?.invoke(key)
    }

    // Main thread. Starts a stream from the PC on Wi‑Fi, proving this phone holds the pairing's key
    // for this connection's challenge. `done` gets the stream's key once the PC accepts, or null
    // (it doesn't know this phone any more, or the connection dropped).
    fun join(longTermKey: ByteArray, port: Int, done: (ByteArray?) -> Unit): Boolean {
        val c = challenge ?: return false
        if (!onWifiPc) return false
        val nonce = ByteArray(16).also { SecureRandom().nextBytes(it) }.joinToString("") { "%02x".format(it) }
        joinedProof = WifiTrust.joinedProof(longTermKey, c, nonce)
        onJoined = { ok -> done(if (ok) WifiTrust.streamKey(longTermKey, c, nonce) else null) }
        send("JOIN port=$port&nonce=$nonce&proof=" + WifiTrust.joinProof(longTermKey, c, nonce, port))
        return true
    }

    private fun finishJoin(ok: Boolean) {
        val done = onJoined
        onJoined = null
        done?.invoke(ok)
    }

    // Called on the main thread with every state line the PC sends.
    fun listen(listener: (Pc) -> Unit) = listeners.add(listener)
    fun unlisten(listener: (Pc) -> Unit) = listeners.remove(listener)

    // Called on the main thread whenever what's playing (or its artwork) changes.
    fun listenMedia(listener: () -> Unit) = mediaListeners.add(listener)
    fun unlistenMedia(listener: () -> Unit) = mediaListeners.remove(listener)

    // Main thread only: every change to what's playing goes through here, so listeners hear it.
    private fun show(next: Presence.Media?, picture: Bitmap?) {
        media = next
        artwork = picture
        mediaListeners.forEach { it() }
    }

    // Asks the PC to start a stream to this phone. The PC answers by opening Sonora with the link
    // and this request token, which `claim` recognises, so the app can skip the number: the phone
    // asked, and the link only ever travels over adb.
    fun connect(): Boolean {
        if (output == null) return false
        val token = ByteArray(16).also { SecureRandom().nextBytes(it) }.joinToString("") { "%02x".format(it) }
        request = token
        requestedAt = SystemClock.elapsedRealtime()
        send("CONNECT request=$token")
        return true
    }

    fun claim(token: String?): Boolean {
        val ok = token != null && token == request && SystemClock.elapsedRealtime() - requestedAt < 60_000
        if (ok) request = null
        return ok
    }

    // Ends the stream on the PC too. Proves this phone holds the stream's key, so no other app on
    // the phone can hang it up.
    fun end(secret: ByteArray) {
        val c = challenge ?: return
        send("END proof=" + Presence.proof(secret, Presence.END, c))
    }

    // The stream's key while this phone streams from the PC over USB, null once it ends. With it
    // the PC lets this connection (and no other app on the phone) see what's playing and control
    // it. Sent again after every reconnect, since each connection has its own challenge.
    fun authorize(secret: ByteArray?) {
        authSecret = secret
        // A new stream counts its frames from zero: the old clock sample means nothing now.
        clock.reset()
        if (secret != null) sendAuth(secret) else show(null, null)
    }

    private fun sendAuth(secret: ByteArray) {
        val c = challenge ?: return
        send("AUTH proof=" + Presence.proof(secret, Presence.AUTH, c))
        burst = 5
        sentVolume = -1
    }

    private fun pingIfDue() {
        if (output == null || authSecret == null) return
        mediaVolume?.invoke()?.let { level ->
            if (level != sentVolume) { sentVolume = level; send("VOLUME level=$level") }
        }
        val now = SystemClock.elapsedRealtime()
        if (burst <= 0 && now - pingedAt < 2000) return
        if (burst > 0) burst--
        pingedAt = now
        // Stamped as it's written, so time spent queued doesn't count.
        writer.execute {
            runCatching { output?.apply { write("PING t=${System.nanoTime() / 1000}\n".toByteArray(Charsets.UTF_8)); flush() } }
        }
    }

    // The PC's own media buttons. The button flips at once; the PC's update confirms it a moment
    // later (and corrects it if the playing app refused).
    fun control(action: String, positionMs: Long? = null) {
        val current = media ?: return
        val now = SystemClock.elapsedRealtime()
        when (action) {
            "toggle" -> {
                if (!current.canToggle) return
                show(current.copy(playing = !current.playing, positionMs = current.position(now), at = now), artwork)
            }
            // Explicit, for headphone buttons: a press can't flip the wrong way when this phone's
            // picture of the state is a moment old.
            "play", "pause" -> {
                if (!current.canToggle) return
                val playing = action == "play"
                if (current.playing != playing) show(current.copy(playing = playing, positionMs = current.position(now), at = now), artwork)
            }
            "previous" -> if (!current.canPrevious) return
            "next" -> if (!current.canNext) return
            "seek" -> {
                if (!current.canSeek || positionMs == null) return
                val target = positionMs.coerceIn(0, current.durationMs)
                show(current.copy(positionMs = target, at = now), artwork)
                send("CONTROL action=seek&position=$target")
                return
            }
            else -> return
        }
        send("CONTROL action=$action")
    }

    // Main thread. Asks the PC for the link to what's playing (the browser tab's address);
    // `done` gets it, or null when the PC has none or hasn't answered within 5 s.
    fun link(done: (String?) -> Unit) {
        if (output == null || media == null) { done(null); return }
        val id = ByteArray(4).also { SecureRandom().nextBytes(it) }.joinToString("") { "%02x".format(it) }
        linkRequest?.let { finishLink(it, null) }
        linkRequest = id
        linkDone = done
        send("LINK request=$id")
        main.postDelayed({ finishLink(id, null) }, 5000)
    }

    private fun finishLink(id: String, url: String?) {
        if (linkRequest != id) return
        val done = linkDone
        linkRequest = null
        linkDone = null
        done?.invoke(url)
    }

    // Main thread. Mutes or unmutes the PC's speakers, so only this phone plays. Shown at once; the
    // PC's next state line confirms it.
    fun mutePc(muted: Boolean) {
        val current = pc ?: return
        if (!current.mine) return
        pc = current.copy(pcMuted = muted)
        send("CONTROL action=" + if (muted) "mute" else "unmute")
    }

    private fun send(line: String) = writer.execute {
        runCatching { output?.apply { write("$line\n".toByteArray(Charsets.UTF_8)); flush() } }
    }

    private enum class Probe { Answered, Refused, Silent }

    private fun loop() {
        var wait = 1500L
        while (true) {
            synchronized(lock) { while (!wanted) lock.wait() }
            val target = host
            val result = probe()
            // No PC: forget it. Released on purpose: keep the last answer so the screen doesn't
            // flicker when the app comes back, and let the next probe correct it.
            if (result != Probe.Answered || wanted) main.post { pc = null }
            // A refusal is local and free. A tunnel with nobody behind it (Sonora quit without
            // cleaning up) costs adb a round trip each time, so back off to 8 s.
            wait = if (result == Probe.Silent) minOf(wait * 2, 8000L) else 1500L
            // Moved to another PC (useWifi): try it at once.
            synchronized(lock) { if (wanted && target == host) lock.wait(wait) }
        }
    }

    // One connection: HELLO, the PC's answer, then its state lines until the cable or the PC goes.
    private fun probe(): Probe {
        val target = host
        // On Wi‑Fi, the connection goes over the Wi‑Fi even when mobile data is the default.
        val s = network?.takeIf { target != LOOPBACK }?.let { runCatching { it.socketFactory.createSocket() }.getOrNull() } ?: Socket()
        socket = s
        if (!wanted || target != host) { runCatching { s.close() }; return Probe.Refused }
        var greeted = false
        try {
            try {
                s.connect(InetSocketAddress(target, PORT), if (target == LOOPBACK) 1000 else 3000)
            } catch (error: IOException) {
                if (target != LOOPBACK) Log.w("Sonora", "channel: can't reach $target:$PORT: $error")
                return Probe.Refused
            }
            s.tcpNoDelay = true
            s.soTimeout = 3000
            val out = s.getOutputStream()
            val input = s.getInputStream().bufferedReader(Charsets.UTF_8)
            val model = URLEncoder.encode(Build.MODEL, "UTF-8").replace("+", "%20")
            val id = phoneId?.let { "&id=$it" } ?: ""
            out.write("HELLO v=1&model=$model$id\n".toByteArray(Charsets.UTF_8))
            out.flush()
            val hello = input.readLine() ?: return Probe.Silent
            if (!hello.startsWith("SONORA ")) return Probe.Silent
            val fields = Presence.fields(hello)
            if (fields["v"] != "1") return Probe.Silent
            val name = fields["name"]?.takeIf { it.isNotBlank() } ?: "Your PC"
            val pcId = fields["id"]
            if (target != LOOPBACK) Log.i("Sonora", "channel: greeted by $name ($pcId) at $target")
            challenge = fields["challenge"]
            connectedHost = target
            output = out
            publish(name, fields, pcId, target)
            authSecret?.let { sendAuth(it) }
            greeted = true
            s.soTimeout = 0
            // Artwork arrives once per picture on this connection, just before the MEDIA line naming it.
            var artId: String? = null
            var art: Bitmap? = null
            while (wanted) {
                val line = input.readLine() ?: break
                val receivedUs = System.nanoTime() / 1000
                when {
                    line.startsWith("PONG ") -> {
                        val f = Presence.fields(line)
                        val sent = f["t"]?.toLongOrNull()
                        val pcTime = f["time"]?.toLongOrNull()
                        if (sent != null && pcTime != null) clock.pong(sent, pcTime, receivedUs)
                        val frame = f["frame"]?.toLongOrNull()
                        val at = f["at"]?.toLongOrNull()
                        if (frame != null && at != null) clock.sample(frame, at)
                    }
                    line.startsWith("STATE ") -> publish(name, Presence.fields(line), pcId, target)
                    // Wi‑Fi pairing: the PC's half of the key agreement, then the user's answer on the PC.
                    line.startsWith("PAIRING ") -> {
                        val key = WifiTrust.fromBase64Url(Presence.fields(line)["key"])?.takeIf { it.size == 64 }?.let { pairing?.agree(it) }
                        agreed = key
                        main.post { if (key == null) finishPair(null) else onCode?.invoke(WifiTrust.code(key)) }
                    }
                    line.startsWith("PAIRED ") -> {
                        val ok = Presence.fields(line)["ok"] == "1"
                        Log.i("Sonora", "channel: pairing ${if (ok) "allowed" else "declined"} on the PC")
                        main.post { finishPair(if (ok) agreed else null) }
                    }
                    line.startsWith("JOINED ") -> {
                        // Accepted only with the PC's own proof: something else answering at this
                        // address in the PC's name gets nowhere.
                        val f = Presence.fields(line)
                        val expected = joinedProof
                        val ok = f["ok"] == "1" && expected != null && WifiTrust.sameProof(f["proof"], expected)
                        if (f["ok"] == "1" && !ok) Log.w("Sonora", "channel: JOINED without the PC's proof; not trusted")
                        Log.i("Sonora", "channel: join ${if (ok) "accepted" else "refused"}")
                        main.post { finishJoin(ok) }
                    }
                    line.startsWith("ART ") -> {
                        val f = Presence.fields(line)
                        decodeArt(f["jpeg"])?.let { artId = f["id"]; art = it }
                    }
                    line.startsWith("LINK ") -> Presence.link(line)?.let { (id, url) -> main.post { finishLink(id, url) } }
                    line.startsWith("MEDIA ") -> {
                        val next = Presence.media(line, SystemClock.elapsedRealtime())
                        val picture = if (next?.artId != null && next.artId == artId) art else null
                        // A line still in flight when the stream ended mustn't bring the card back.
                        main.post { if (next == null || authSecret != null) show(next, picture) }
                    }
                }
            }
            return Probe.Answered
        } catch (_: IOException) {
            return if (greeted) Probe.Answered else Probe.Silent
        } finally {
            output = null
            challenge = null
            connectedHost = null
            clock.reset()
            runCatching { s.close() }
            if (greeted) main.post { show(null, null) }
            // Whatever was waiting on this connection won't hear back now.
            main.post { if (onPaired != null) finishPair(null); if (onJoined != null) finishJoin(false) }
        }
    }

    private fun decodeArt(data: String?): Bitmap? {
        // At most 160 KB of JPEG, base64-encoded.
        if (data.isNullOrEmpty() || data.length > 220_000) return null
        val bytes = runCatching { Base64.getUrlDecoder().decode(data) }.getOrNull() ?: return null
        return runCatching { BitmapFactory.decodeByteArray(bytes, 0, bytes.size) }.getOrNull()
    }

    private fun publish(name: String, f: Map<String, String>, id: String?, from: String) {
        val next = Pc(name, f["state"] ?: "offline", f["you"] == "1", f["busy"] == "1", f["problem"], id, from, f["pcmuted"] == "1")
        main.post {
            pc = next
            listeners.forEach { it(next) }
        }
    }

}
