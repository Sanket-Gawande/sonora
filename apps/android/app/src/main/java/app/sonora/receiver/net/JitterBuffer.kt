package app.sonora.receiver.net

// Reorders 5 ms packets by sequence and releases them at a steady pace. Playback waits until
// `targetDepth` packets are queued, fills a missing packet by fading the previous one out, drops
// packets that arrive after their slot was played, and trims itself if it grows too deep.
class JitterBuffer(private val packetBytes: Int, private val targetDepth: Int = 8, private val maxDepth: Int = 40) {
    private val pending = sortedMapOf<Long, ByteArray>()
    private var next = -1L
    private var started = false
    private var last: ByteArray? = null

    var played = 0L; private set
    var concealed = 0L; private set
    var late = 0L; private set
    var trimmed = 0L; private set
    // The sequence of the packet `pull` just returned; -1 for a filled gap or while pre-buffering.
    var lastSequence = -1L; private set
    val depth: Int get() = synchronized(this) { pending.size }

    @Synchronized
    fun push(sequence: Long, pcm: ByteArray) {
        if (pcm.size != packetBytes) return
        if (next >= 0 && sequence < next) { late++; return }
        pending[sequence] = pcm
        // Too deep means the sender is ahead of us (clock drift or a burst): skip to catch up.
        while (pending.size > maxDepth) {
            pending.remove(pending.firstKey())
            trimmed++
            next = pending.firstKey()
        }
    }

    // Returns the next 5 ms to play, or null while pre-buffering.
    @Synchronized
    fun pull(): ByteArray? {
        lastSequence = -1
        if (!started) {
            if (pending.size < targetDepth) return null
            started = true
            next = pending.firstKey()
        }
        if (pending.isEmpty()) {
            // Ran dry: go back to pre-buffering rather than stuttering.
            started = false
            return null
        }
        val packet = pending.remove(next)
        next++
        played++
        if (packet != null) { last = packet; lastSequence = next - 1; return packet }
        concealed++
        return fadeOut(last)
    }

    // A lost packet becomes the previous packet faded to silence, which hides short gaps.
    private fun fadeOut(previous: ByteArray?): ByteArray {
        val out = ByteArray(packetBytes)
        if (previous == null) return out
        val samples = packetBytes / 2
        for (i in 0 until samples) {
            val v = (previous[i * 2].toInt() and 0xFF) or (previous[i * 2 + 1].toInt() shl 8)
            val scaled = (v * (samples - i) / samples)
            out[i * 2] = scaled.toByte()
            out[i * 2 + 1] = (scaled shr 8).toByte()
        }
        last = out
        return out
    }
}
