package app.sonora.receiver.net

import java.nio.ByteBuffer
import java.security.MessageDigest
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.IvParameterSpec
import javax.crypto.spec.SecretKeySpec

// Opens (and, for tests, seals) audio packets as specified in docs/protocol.md:
// AES-256-CTR, truncated HMAC-SHA256 tag over header and ciphertext, 64-packet replay window.
class PacketCodec(secret: ByteArray) {
    enum class Result { Ok, Malformed, BadTag, Replay }

    data class Packet(val streamId: Int, val sequence: Long, val timestamp: Long, val silence: Boolean, val pcm: ByteArray)

    private val encKey: SecretKeySpec
    private val mac: Mac
    private val cipher = Cipher.getInstance("AES/CTR/NoPadding")
    private var highest = 0L
    private var window = 0L
    private var any = false

    init {
        require(secret.size == 32) { "The session secret must be 32 bytes." }
        val kdf = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(secret, "HmacSHA256")) }
        encKey = SecretKeySpec(kdf.doFinal("sonora-v1-enc".toByteArray(Charsets.US_ASCII)), "AES")
        val macKey = kdf.doFinal("sonora-v1-mac".toByteArray(Charsets.US_ASCII))
        mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(macKey, "HmacSHA256")) }
    }

    fun seal(streamId: Int, sequence: Long, timestamp: Long, silence: Boolean, pcm: ByteArray): ByteArray {
        val packet = ByteBuffer.allocate(HEADER + pcm.size + TAG)
            .put(0x53).put(0x4E).put(VERSION).put(if (silence) 1 else 0)
            .putInt(streamId).putInt(sequence.toInt()).putLong(timestamp)
            .put(crypt(pcm, streamId, sequence))
            .array()
        mac.update(packet, 0, HEADER + pcm.size)
        mac.doFinal().copyInto(packet, HEADER + pcm.size, 0, TAG)
        return packet
    }

    fun open(data: ByteArray, length: Int = data.size): Pair<Result, Packet?> {
        if (length < HEADER + TAG || data[0] != 0x53.toByte() || data[1] != 0x4E.toByte() || data[2] != VERSION) return Result.Malformed to null
        val count = length - HEADER - TAG
        mac.update(data, 0, HEADER + count)
        val expected = mac.doFinal().copyOf(TAG)
        if (!MessageDigest.isEqual(expected, data.copyOfRange(HEADER + count, HEADER + count + TAG))) return Result.BadTag to null
        val header = ByteBuffer.wrap(data, 0, HEADER)
        header.position(4)
        val streamId = header.int
        val sequence = header.int.toLong() and 0xFFFFFFFFL
        val timestamp = header.long
        if (!accept(sequence)) return Result.Replay to null
        val pcm = crypt(data.copyOfRange(HEADER, HEADER + count), streamId, sequence)
        return Result.Ok to Packet(streamId, sequence, timestamp, (data[3].toInt() and 1) != 0, pcm)
    }

    fun resetReplayWindow() { any = false; window = 0; highest = 0 }

    private fun accept(sequence: Long): Boolean {
        if (!any) { any = true; highest = sequence; window = 1; return true }
        if (sequence > highest) {
            val shift = sequence - highest
            window = if (shift >= 64) 1L else (window shl shift.toInt()) or 1L
            highest = sequence
            return true
        }
        val age = highest - sequence
        if (age >= 64) return false
        val bit = 1L shl age.toInt()
        if (window and bit != 0L) return false
        window = window or bit
        return true
    }

    // Counter block: streamId (4) ‖ sequence (4) ‖ block index (8). Java's CTR increments the
    // whole block big-endian, which matches while the low 8 bytes don't overflow.
    private fun crypt(input: ByteArray, streamId: Int, sequence: Long): ByteArray {
        val iv = ByteBuffer.allocate(16).putInt(streamId).putInt(sequence.toInt()).putLong(0).array()
        cipher.init(Cipher.ENCRYPT_MODE, encKey, IvParameterSpec(iv))
        return cipher.doFinal(input)
    }

    companion object {
        const val HEADER = 20
        const val TAG = 16
        const val VERSION: Byte = 1
    }
}
