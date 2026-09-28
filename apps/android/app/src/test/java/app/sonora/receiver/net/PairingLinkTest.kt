package app.sonora.receiver.net

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PairingLinkTest {
    private val vector: Map<String, String> = javaClass.classLoader!!.getResourceAsStream("vector.txt")!!
        .bufferedReader().readLines().filter { '=' in it }.associate { it.substringBefore('=') to it.substringAfter('=') }

    @Test
    fun readsTheWindowsLinkAndShowsTheSameNumber() {
        val link = PairingLink.parse(vector.getValue("link"))!!
        assertEquals("192.168.1.20", link.host)
        assertEquals(47210, link.port)
        assertEquals("Studio PC", link.name)
        assertArrayEquals(ByteArray(32) { it.toByte() }, link.secret)
        assertEquals(vector.getValue("number"), link.number)
    }

    @Test
    fun rejectsMalformedLinks() {
        assertNull(PairingLink.parse("https://example.com"))
        assertNull(PairingLink.parse("sonora://pair?v=2&h=a&p=1&k=AAAA"))
        assertNull(PairingLink.parse("sonora://pair?v=1&h=a&p=99999&k=AAAA"))
        assertNull(PairingLink.parse("sonora://pair?v=1&h=a&p=1&k=AAAA"))
    }
}
