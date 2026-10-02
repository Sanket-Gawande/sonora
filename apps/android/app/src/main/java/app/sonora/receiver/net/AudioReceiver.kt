package app.sonora.receiver.net

import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioRouting
import android.media.AudioTimestamp
import android.media.AudioTrack
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import java.io.BufferedInputStream
import java.io.DataInputStream
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketException
import java.net.SocketTimeoutException
import kotlin.concurrent.thread

// Receives sealed audio packets and plays them through a low-latency AudioTrack. Only packets
// that pass the codec's tag and replay checks ever reach the speaker.
//  - host == null: listen for UDP datagrams on `port` (Wi-Fi).
//  - host != null: connect over TCP and read [u16 length][packet] frames (USB via `adb reverse`).
class AudioReceiver(secret: ByteArray, private val host: String?, private val port: Int) {
    data class Stats(
        val connected: Boolean,
        val received: Long,
        val audible: Long,
        val rejected: Long,
        val played: Long,
        val concealed: Long,
        val late: Long,
        val bufferedMs: Int,
    )

    private val codec = PacketCodec(secret)
    // Starts low and adapts (JitterBuffer), latency first: a cable starts at 25 ms of cushion,
    // Wi‑Fi at 40 ms; either grows when the connection makes playback run dry (Wi‑Fi up to 200 ms on
    // a busy network) and creeps back down to 15-20 ms while it's steady.
    private val jitter = if (host == null) JitterBuffer(PACKET_BYTES, targetDepth = 8, maxDepth = 80, floor = 4, ceiling = 40)
    else JitterBuffer(PACKET_BYTES, targetDepth = 5, maxDepth = 40, floor = 3, ceiling = 20)
    // UDP has no connection: "connected" there means a packet arrived in the last 1.5 s.
    @Volatile private var lastPacket = 0L
    @Volatile private var running = false
    @Volatile private var connected = false
    // The output picked in the player (null: Android decides), and the one Android really plays on.
    @Volatile var preferred: AudioDeviceInfo? = null
    @Volatile var routed: AudioDeviceInfo? = null; private set
    // When this phone's speaker plays a recent packet: its first frame on the stream's timeline
    // (sequence × 240, the PC's own count) and the time, System.nanoTime in µs. From the track's
    // own presentation timestamps, twice a second. Null until the track reports one.
    class Playout(val frame: Long, val atUs: Long)
    @Volatile var playout: Playout? = null; private set
    private var received = 0L
    private var audible = 0L
    private var rejected = 0L
    @Volatile private var closer: (() -> Unit)? = null

    fun stats() = Stats(if (host == null) SystemClock.elapsedRealtime() - lastPacket < 1500 else connected, received, audible, rejected, jitter.played, jitter.concealed, jitter.late, jitter.depth * PACKET_MS)

    fun start() {
        running = true
        thread(name = "Sonora receive", isDaemon = true) { if (host == null) receiveUdp() else receiveTcp(host) }
        thread(name = "Sonora playback", isDaemon = true, priority = Thread.MAX_PRIORITY) { play() }
    }

    private fun handle(buffer: ByteArray, length: Int) {
        val (result, packet) = codec.open(buffer, length)
        if (result != PacketCodec.Result.Ok || packet == null) { rejected++; return }
        received++
        lastPacket = SystemClock.elapsedRealtime()
        if (!packet.silence) audible++
        jitter.push(packet.sequence, packet.pcm)
    }

    private fun receiveUdp() {
        val udp = DatagramSocket(port).apply { soTimeout = 500; receiveBufferSize = 1 shl 18 }
        closer = { udp.close() }
        connected = true
        val buffer = ByteArray(2048)
        val datagram = DatagramPacket(buffer, buffer.size)
        while (running) {
            try {
                udp.receive(datagram)
                handle(buffer, datagram.length)
            } catch (_: SocketTimeoutException) {
            } catch (_: SocketException) { break }
        }
        connected = false
    }

    private fun receiveTcp(host: String) {
        val buffer = ByteArray(4096)
        var wait = 500L
        while (running) {
            var frames = 0
            val socket = Socket()
            closer = { socket.close() }
            try {
                socket.tcpNoDelay = true
                socket.connect(InetSocketAddress(host, port), 2000)
                socket.soTimeout = 3000
                connected = true
                val input = DataInputStream(BufferedInputStream(socket.getInputStream()))
                while (running) {
                    val length = input.readUnsignedShort()
                    if (length > buffer.size) break
                    input.readFully(buffer, 0, length)
                    handle(buffer, length)
                    frames++
                }
            } catch (_: IOException) {
            } finally {
                connected = false
                runCatching { socket.close() }
            }
            // The PC may not be listening yet, or the cable was unplugged: retry, backing off to 5 s
            // while nothing answers, since each try through adb's tunnel costs the adb server work.
            wait = if (frames > 0) 500L else minOf(wait * 2, 5000L)
            if (running) Thread.sleep(if (frames > 0) 250L else wait)
        }
    }

    private fun play() {
        val format = AudioFormat.Builder()
            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setSampleRate(SAMPLE_RATE)
            .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
            .build()
        val minimum = AudioTrack.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_OUT_STEREO, AudioFormat.ENCODING_PCM_16BIT)
        val track = AudioTrack.Builder()
            .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_MUSIC).build())
            .setAudioFormat(format)
            .setBufferSizeInBytes(maxOf(minimum, PACKET_BYTES * 4))
            .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build()
        // Route changes arrive on the main thread, off the audio path.
        val routing = AudioRouting.OnRoutingChangedListener { router -> routed = runCatching { router.routedDevice }.getOrNull() }
        track.addOnRoutingChangedListener(routing, Handler(Looper.getMainLooper()))
        var applied = preferred
        track.setPreferredDevice(applied)
        track.play()
        routed = track.routedDevice
        // Android sizes even a low-latency track for the worst case (80 ms on a moto g82), and a
        // blocking writer keeps it full: allow three packets (15 ms) queued, and give back one more
        // packet of room whenever the speaker actually runs short.
        var room = runCatching { track.setBufferSizeInFrames(PACKET_FRAMES * 3) }.getOrDefault(-1)
        var underruns = track.underrunCount
        var checked = 0
        val silence = ByteArray(PACKET_BYTES)
        val stamp = AudioTimestamp()
        var written = 0L
        var stampedAt = 0L
        try {
            while (running) {
                val wanted = preferred
                if (wanted !== applied) { applied = wanted; track.setPreferredDevice(wanted) }
                val next = jitter.pull()
                val sequence = jitter.lastSequence
                val now = System.nanoTime()
                if (sequence >= 0 && now - stampedAt > 500_000_000L && track.getTimestamp(stamp)) {
                    stampedAt = now
                    // This packet's first frame is frame `written` of the track; the timestamp says
                    // when frame `stamp.framePosition` left the speaker.
                    val playsAt = stamp.nanoTime + (written - stamp.framePosition) * 1_000_000_000L / SAMPLE_RATE
                    playout = Playout(sequence * PACKET_FRAMES, playsAt / 1000)
                }
                // Blocking write paces playback at the device's real rate.
                track.write(next ?: silence, 0, PACKET_BYTES)
                written += PACKET_FRAMES
                if (room > 0 && ++checked >= 40) {
                    checked = 0
                    val count = track.underrunCount
                    if (count > underruns && room < track.bufferCapacityInFrames) {
                        room = track.setBufferSizeInFrames(minOf(track.bufferCapacityInFrames, room + PACKET_FRAMES))
                    }
                    underruns = count
                }
            }
        } finally {
            track.removeOnRoutingChangedListener(routing)
            track.stop()
            track.release()
        }
    }

    fun stop() {
        running = false
        closer?.invoke()
    }

    companion object {
        const val SAMPLE_RATE = 48_000
        const val PACKET_MS = 5
        const val PACKET_FRAMES = SAMPLE_RATE / 1000 * PACKET_MS
        const val PACKET_BYTES = SAMPLE_RATE / 1000 * PACKET_MS * 2 * 2
    }
}
