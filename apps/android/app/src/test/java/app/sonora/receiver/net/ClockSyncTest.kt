package app.sonora.receiver.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ClockSyncTest {
    // The PC's clock runs 10 s ahead of the phone's.
    private val ahead = 10_000_000L

    @Test
    fun nothingUntilBothClocksAreKnown() {
        val clock = ClockSync()
        assertNull(clock.latencyMs(0, 1_000_000))
        clock.pong(sentUs = 1_000_000, pcUs = 1_000_500 + ahead, receivedUs = 1_001_000)
        assertNull(clock.latencyMs(0, 1_000_000))
        clock.sample(48_000, 2_000_000 + ahead)
        assertEquals(80, clock.latencyMs(48_000, 2_080_000))
    }

    @Test
    fun framesCountFromTheSample() {
        val clock = ClockSync()
        clock.pong(1_000_000, 1_000_500 + ahead, 1_001_000)
        // Frame 96 000 was mixed at t = 2 s (PC); frame 48 000 therefore a second earlier.
        clock.sample(96_000, 2_000_000 + ahead)
        assertEquals(65, clock.latencyMs(48_000, 1_065_000))
    }

    @Test
    fun theQuickestRoundTripWins() {
        val clock = ClockSync()
        clock.sample(0, ahead)
        // A slow, lopsided round trip (the reply was held up) would put the offset 20 ms off...
        clock.pong(sentUs = 0, pcUs = ahead + 5_000, receivedUs = 50_000)
        // ...a quick one pins it within half a millisecond.
        clock.pong(sentUs = 100_000, pcUs = ahead + 100_500, receivedUs = 101_000)
        assertEquals(70, clock.latencyMs(0, 70_000))
        assertEquals(0, clock.uncertaintyMs())
    }

    @Test
    fun implausibleReadingsAreDropped() {
        val clock = ClockSync()
        clock.pong(0, ahead, 0)
        clock.sample(0, ahead)
        assertNull(clock.latencyMs(0, -5_000))         // before it was mixed
        assertNull(clock.latencyMs(0, 60_000_000))      // a minute late: not a real reading
        clock.pong(0, ahead, 400_000)                    // a round trip too slow to trust is ignored
        assertEquals(0, clock.uncertaintyMs())
    }

    @Test
    fun resetForgetsEverything() {
        val clock = ClockSync()
        clock.pong(0, ahead, 1_000)
        clock.sample(0, ahead)
        clock.reset()
        assertNull(clock.latencyMs(0, 50_000))
        assertNull(clock.uncertaintyMs())
    }
}
