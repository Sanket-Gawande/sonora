package app.sonora.receiver.net

// Puts this phone's clock beside the PC's, for the latency reading (docs/protocol.md, "Latency").
// PING/PONG round trips give the offset between the clocks; the quickest of the last few wins,
// since a quick round trip leaves the least room for error (at most half of it). The PC's clock
// sample says when Windows mixed a given frame of the stream. Together they turn "this phone plays
// frame F at time T" into milliseconds. Microseconds throughout; thread-safe.
class ClockSync(private val sampleRate: Int = 48_000) {
    private class Sync(val roundTrip: Long, val offset: Long)

    private val syncs = ArrayDeque<Sync>()
    private var frame = -1L
    private var mixedAt = 0L

    @Synchronized
    fun reset() {
        syncs.clear()
        frame = -1
    }

    // A PING sent at `sentUs` and answered at `receivedUs` (this phone's clock), stamped `pcUs` by the PC.
    @Synchronized
    fun pong(sentUs: Long, pcUs: Long, receivedUs: Long) {
        val roundTrip = receivedUs - sentUs
        if (roundTrip < 0 || roundTrip > 250_000) return
        syncs.addLast(Sync(roundTrip, pcUs - (sentUs + receivedUs) / 2))
        while (syncs.size > KEEP) syncs.removeFirst()
    }

    // The PC's clock sample: stream frame `frame` was mixed at `pcUs`.
    @Synchronized
    fun sample(frame: Long, pcUs: Long) {
        if (frame < 0) return
        this.frame = frame
        mixedAt = pcUs
    }

    // Milliseconds from Windows mixing stream frame `frame` to this phone playing it at `playedUs`
    // (this phone's clock). Null until both clocks are known, and for anything implausible.
    @Synchronized
    fun latencyMs(frame: Long, playedUs: Long): Int? {
        if (this.frame < 0 || syncs.isEmpty()) return null
        val offset = syncs.minBy { it.roundTrip }.offset
        val mixed = mixedAt + (frame - this.frame) * 1_000_000 / sampleRate
        val ms = (playedUs + offset - mixed) / 1000
        return if (ms in 0..MAX_MS) ms.toInt() else null
    }

    // The error bound: half the quickest round trip, in ms.
    @Synchronized
    fun uncertaintyMs(): Int? = syncs.minOfOrNull { it.roundTrip }?.let { (it / 2000).toInt() }

    companion object {
        const val KEEP = 8
        const val MAX_MS = 5_000L
    }
}
