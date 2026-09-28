package app.sonora.receiver.net

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class JitterBufferTest {
    private fun packet(value: Int) = ByteArray(8) { value.toByte() }

    @Test
    fun waitsForTargetDepthThenPlaysInOrder() {
        val buffer = JitterBuffer(packetBytes = 8, targetDepth = 3)
        buffer.push(1, packet(1))
        buffer.push(0, packet(0))
        assertNull(buffer.pull())
        buffer.push(2, packet(2))
        assertArrayEquals(packet(0), buffer.pull())
        assertArrayEquals(packet(1), buffer.pull())
        assertArrayEquals(packet(2), buffer.pull())
    }

    @Test
    fun concealsALostPacketAndDropsItWhenItArrivesLate() {
        val buffer = JitterBuffer(packetBytes = 8, targetDepth = 2)
        buffer.push(0, packet(100))
        buffer.push(2, packet(2))
        buffer.pull()
        val filler = buffer.pull()!!
        assertEquals(1L, buffer.concealed)
        // Starts at the previous packet's level and fades towards silence.
        val first = (filler[0].toInt() and 0xFF) or (filler[1].toInt() shl 8)
        val lastSample = (filler[6].toInt() and 0xFF) or (filler[7].toInt() shl 8)
        assertEquals(0x6464, first)
        assertEquals(0x6464 / 4, lastSample)
        buffer.push(1, packet(1))
        assertEquals(1L, buffer.late)
        assertArrayEquals(packet(2), buffer.pull())
    }

    @Test
    fun trimsWhenTheSenderRunsAhead() {
        val buffer = JitterBuffer(packetBytes = 8, targetDepth = 2, maxDepth = 4)
        for (i in 0L until 10L) buffer.push(i, packet(i.toInt()))
        assertEquals(4, buffer.depth)
        assertEquals(6L, buffer.trimmed)
        assertArrayEquals(packet(6), buffer.pull())
    }

    @Test
    fun rebuffersAfterRunningDry() {
        val buffer = JitterBuffer(packetBytes = 8, targetDepth = 2)
        buffer.push(0, packet(0)); buffer.push(1, packet(1))
        buffer.pull(); buffer.pull()
        assertNull(buffer.pull())
        buffer.push(2, packet(2))
        assertNull(buffer.pull())
        buffer.push(3, packet(3))
        assertArrayEquals(packet(2), buffer.pull())
    }
}
