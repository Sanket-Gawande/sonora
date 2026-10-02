package app.sonora.receiver.net

// Reorders 5 ms packets by sequence and releases them at a steady pace. Playback waits until
// `target` packets are queued, fills a missing packet by fading the previous one out, and drops
// packets that arrive after their slot was played.
//
// The delay stays near the target rather than growing with every hiccup: a buffer that has stayed
// deeper than the target for a second (a burst after a network stall) sheds the excess, quiet
// packets first, so the extra delay doesn't linger. The target itself adapts between `floor` and
// `ceiling`: it rises when playback runs dry (the network needs more cushion) and falls back one
// packet after each 10 s without. `maxDepth` is the hard limit.
class JitterBuffer(
    private val packetBytes: Int,
    targetDepth: Int = 8,
    private val maxDepth: Int = 40,
    private val floor: Int = targetDepth,
    private val ceiling: Int = targetDepth,
) {
    private val pending = sortedMapOf<Long, ByteArray>()
    private var next = -1L
    private var started = false
    private var last: ByteArray? = null
    // Depth watching: the shallowest the buffer got over the last second, and how much to shed.
    private var lowWater = Int.MAX_VALUE
    private var pulls = 0
    private var calm = 0
    private var shed = 0
    private var shedWait = 0

    var target = targetDepth; private set
    var played = 0L; private set
    var concealed = 0L; private set
    var late = 0L; private set
    var trimmed = 0L; private set
    var underruns = 0L; private set
    // The sequence of the packet `pull` just returned; -1 for a filled gap or while pre-buffering.
    var lastSequence = -1L; private set
    val depth: Int get() = synchronized(this) { pending.size }

    @Synchronized
    fun push(sequence: Long, pcm: ByteArray) {
        if (pcm.size != packetBytes) return
        if (next >= 0 && sequence < next) { late++; return }
        pending[sequence] = pcm
        // Far too deep means the sender is well ahead of us: skip to catch up at once.
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
            if (pending.size < target) return null
            started = true
            next = pending.firstKey()
        }
        if (pending.isEmpty()) {
            // Ran dry: more cushion, and pre-buffer again rather than stutter.
            started = false
            underruns++
            target = minOf(ceiling, target + 2)
            calm = 0
            shed = 0
            return null
        }
        settle()
        val packet = pending.remove(next)
        next++
        played++
        if (packet != null) { last = packet; lastSequence = next - 1; return packet }
        concealed++
        return fadeOut(last)
    }

    // Once a second: anything the buffer never dipped below, beyond the target, is delay to shed.
    // Every 10 s without running dry, one packet less of cushion.
    private fun settle() {
        lowWater = minOf(lowWater, pending.size)
        if (++pulls >= WINDOW) {
            if (lowWater > target + 1) shed = lowWater - target
            lowWater = Int.MAX_VALUE
            pulls = 0
        }
        if (++calm >= CALM && target > floor) { target--; calm = 0 }
        if (shed > 0 && pending.size > target) {
            val candidate = pending[next]
            // A quiet packet goes unnoticed; a loud one only after waiting a while for a quiet one.
            if (candidate != null && (quiet(candidate) || ++shedWait > 40)) {
                pending.remove(next)
                next++
                trimmed++
                shed--
                shedWait = 0
            }
        } else if (shed > 0) {
            shed = 0
        }
    }

    private fun quiet(pcm: ByteArray): Boolean {
        var i = 0
        while (i + 1 < pcm.size) {
            val v = (pcm[i].toInt() and 0xFF) or (pcm[i + 1].toInt() shl 8)
            if (v > QUIET || v < -QUIET) return false
            i += 8
        }
        return true
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

    private companion object {
        const val WINDOW = 200 // pulls: one second of 5 ms packets
        const val CALM = 2000 // ten seconds
        const val QUIET = 600 // about -35 dBFS
    }
}
