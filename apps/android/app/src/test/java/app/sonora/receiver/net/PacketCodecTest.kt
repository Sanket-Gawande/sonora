package app.sonora.receiver.net

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test

// vector.txt is produced by the Windows app (`Sonora.exe --transport-test`), so these tests
// prove both implementations agree byte for byte.
class PacketCodecTest {
    private val vector: Map<String, String> = javaClass.classLoader!!.getResourceAsStream("vector.txt")!!
        .bufferedReader().readLines().filter { '=' in it }.associate { it.substringBefore('=') to it.substringAfter('=') }

    private fun hex(s: String) = ByteArray(s.length / 2) { s.substring(it * 2, it * 2 + 2).toInt(16).toByte() }

    @Test
    fun opensThePacketSealedOnWindows() {
        val (result, packet) = PacketCodec(hex(vector.getValue("secret"))).open(hex(vector.getValue("packet")))
        assertEquals(PacketCodec.Result.Ok, result)
        assertEquals(vector.getValue("streamId").toInt(), packet!!.streamId)
        assertEquals(vector.getValue("sequence").toLong(), packet.sequence)
        assertEquals(vector.getValue("timestamp").toLong(), packet.timestamp)
        assertArrayEquals(hex(vector.getValue("plaintext")), packet.pcm)
    }

    @Test
    fun sealsExactlyLikeWindows() {
        val sealed = PacketCodec(hex(vector.getValue("secret"))).seal(
            vector.getValue("streamId").toInt(), vector.getValue("sequence").toLong(), vector.getValue("timestamp").toLong(), false,
            hex(vector.getValue("plaintext")),
        )
        assertArrayEquals(hex(vector.getValue("packet")), sealed)
    }

    @Test
    fun rejectsTamperingReplayAndForeignKeys() {
        val secret = hex(vector.getValue("secret"))
        val packet = hex(vector.getValue("packet"))
        val codec = PacketCodec(secret)
        assertEquals(PacketCodec.Result.Ok, codec.open(packet.copyOf()).first)
        assertEquals(PacketCodec.Result.Replay, codec.open(packet.copyOf()).first)
        val tampered = packet.copyOf().also { it[PacketCodec.HEADER + 3] = (it[PacketCodec.HEADER + 3].toInt() xor 1).toByte() }
        assertEquals(PacketCodec.Result.BadTag, PacketCodec(secret).open(tampered).first)
        assertEquals(PacketCodec.Result.BadTag, PacketCodec(ByteArray(32)).open(packet.copyOf()).first)
        assertEquals(PacketCodec.Result.Malformed, codec.open(ByteArray(10)).first)
    }
}
