package app.sonora.receiver.net

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
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

// Finds Sonora on the PC this phone is plugged into. Sonora on Windows watches for phones with
// USB debugging on and runs `adb reverse tcp:47211` for each, so while the cable is in, the PC
// answers on this phone's own loopback; nothing here touches the network. Through it the phone
// asks the PC to connect and ends the stream, and, while it holds the stream, sees what's playing
// on the PC and controls it. docs/protocol.md, "Finding the PC over USB".
object PcFinder {
    const val PORT = 47_211

    // What the PC says. `mine`: its stream is with this phone. `busy`: it's streaming to another
    // phone. `problem`: why the PC's last attempt to connect this phone failed.
    data class Pc(val name: String, val state: String, val mine: Boolean, val busy: Boolean, val problem: String?)

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
    }

    private fun pingIfDue() {
        if (output == null || authSecret == null) return
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

    private fun send(line: String) = writer.execute {
        runCatching { output?.apply { write("$line\n".toByteArray(Charsets.UTF_8)); flush() } }
    }

    private enum class Probe { Answered, Refused, Silent }

    private fun loop() {
        var wait = 1500L
        while (true) {
            synchronized(lock) { while (!wanted) lock.wait() }
            val result = probe()
            // No PC: forget it. Released on purpose: keep the last answer so the screen doesn't
            // flicker when the app comes back, and let the next probe correct it.
            if (result != Probe.Answered || wanted) main.post { pc = null }
            // A refusal is local and free. A tunnel with nobody behind it (Sonora quit without
            // cleaning up) costs adb a round trip each time, so back off to 8 s.
            wait = if (result == Probe.Silent) minOf(wait * 2, 8000L) else 1500L
            synchronized(lock) { if (wanted) lock.wait(wait) }
        }
    }

    // One connection: HELLO, the PC's answer, then its state lines until the cable or the PC goes.
    private fun probe(): Probe {
        val s = Socket()
        socket = s
        if (!wanted) return Probe.Refused
        var greeted = false
        try {
            try {
                s.connect(InetSocketAddress("127.0.0.1", PORT), 1000)
            } catch (_: IOException) {
                return Probe.Refused
            }
            s.tcpNoDelay = true
            s.soTimeout = 3000
            val out = s.getOutputStream()
            val input = s.getInputStream().bufferedReader(Charsets.UTF_8)
            val model = URLEncoder.encode(Build.MODEL, "UTF-8").replace("+", "%20")
            out.write("HELLO v=1&model=$model\n".toByteArray(Charsets.UTF_8))
            out.flush()
            val hello = input.readLine() ?: return Probe.Silent
            if (!hello.startsWith("SONORA ")) return Probe.Silent
            val fields = Presence.fields(hello)
            if (fields["v"] != "1") return Probe.Silent
            val name = fields["name"]?.takeIf { it.isNotBlank() } ?: "Your PC"
            challenge = fields["challenge"]
            output = out
            publish(name, fields)
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
                    line.startsWith("STATE ") -> publish(name, Presence.fields(line))
                    line.startsWith("ART ") -> {
                        val f = Presence.fields(line)
                        decodeArt(f["jpeg"])?.let { artId = f["id"]; art = it }
                    }
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
            clock.reset()
            runCatching { s.close() }
            if (greeted) main.post { show(null, null) }
        }
    }

    private fun decodeArt(data: String?): Bitmap? {
        // At most 160 KB of JPEG, base64-encoded.
        if (data.isNullOrEmpty() || data.length > 220_000) return null
        val bytes = runCatching { Base64.getUrlDecoder().decode(data) }.getOrNull() ?: return null
        return runCatching { BitmapFactory.decodeByteArray(bytes, 0, bytes.size) }.getOrNull()
    }

    private fun publish(name: String, f: Map<String, String>) {
        val next = Pc(name, f["state"] ?: "offline", f["you"] == "1", f["busy"] == "1", f["problem"])
        main.post {
            pc = next
            listeners.forEach { it(next) }
        }
    }

}
