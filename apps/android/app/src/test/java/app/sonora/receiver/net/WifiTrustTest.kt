package app.sonora.receiver.net

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

// Wi‑Fi pairing on the phone's side against the vector Windows writes (`Sonora.exe --presence-test`):
// from the same phone key and the PC's public key, the same long-term key, number, join proof and
// stream key.
class WifiTrustTest {
    private val vector: Map<String, String> =
        javaClass.getResource("/presence-vector.txt")!!.readText().lines().filter { '=' in it }
            .associate { it.substringBefore('=') to it.substringAfter('=') }

    private fun bytes(key: String) = WifiTrust.fromBase64Url(vector.getValue(key))!!

    @Test
    fun agreesTheSameKeyAsWindows() {
        val pairing = WifiTrust.pairing(bytes("wifi_phone_private"), bytes("wifi_phone_public"))
        assertArrayEquals(bytes("wifi_phone_public"), pairing.public)
        val key = pairing.agree(bytes("wifi_pc_public"))
        assertArrayEquals(bytes("wifi_key"), key)
        assertEquals(vector["wifi_code"], WifiTrust.code(key!!))
    }

    @Test
    fun joinsWithTheSameProofAndStreamKey() {
        val key = bytes("wifi_key")
        val challenge = vector.getValue("wifi_challenge")
        val nonce = vector.getValue("wifi_nonce")
        val port = vector.getValue("wifi_port").toInt()
        assertEquals(vector["wifi_proof"], WifiTrust.joinProof(key, challenge, nonce, port))
        assertArrayEquals(bytes("wifi_stream"), WifiTrust.streamKey(key, challenge, nonce))
        assertEquals(vector["wifi_joined"], WifiTrust.joinedProof(key, challenge, nonce))
    }

    @Test
    fun freshPairingsAgreeWithEachOther() {
        val phone = WifiTrust.pairing()
        assertEquals(64, phone.public.size)
        assertNotNull(phone.agree(WifiTrust.pairing().public))
    }

    @Test
    fun refusesAKeyOffTheCurve() {
        assertNull(WifiTrust.pairing().agree(ByteArray(64)))
        assertNull(WifiTrust.pairing().agree(ByteArray(10)))
    }

    @Test
    fun readsThePcsAnswerToTheQuery() {
        assertEquals(Presence.Answer("00112233aabbccdd", "Test PC", 47211), Presence.answer("SONORA! v=1&name=Test%20PC&id=00112233aabbccdd&port=47211"))
        assertNull(Presence.answer("SONORA! v=2&name=x&id=00112233aabbccdd&port=47211"))
        assertNull(Presence.answer("SONORA! v=1&name=x&id=nothex&port=47211"))
        assertNull(Presence.answer("SONORA! v=1&name=x&id=00112233aabbccdd&port=0"))
        assertNull(Presence.answer("SONORA? v=1"))
    }
}
