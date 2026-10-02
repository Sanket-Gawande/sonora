package app.sonora.receiver.net

import java.math.BigInteger
import java.security.AlgorithmParameters
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.interfaces.ECPrivateKey
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPrivateKeySpec
import java.security.spec.ECPublicKeySpec
import java.util.Base64
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

// Pairing with a PC over Wi‑Fi (docs/protocol.md, "Wi‑Fi"): ECDH on P-256, a six-digit number both
// screens show, then a long-term key that never travels. Each stream's key, and the proof that
// lets this phone start one, come from it and the connection's challenge. Free of Android, so the
// unit tests check it against the PC's own vector.
object WifiTrust {
    private val curve: ECParameterSpec by lazy {
        AlgorithmParameters.getInstance("EC").apply { init(ECGenParameterSpec("secp256r1")) }.getParameterSpec(ECParameterSpec::class.java)
    }

    // This phone's half of one pairing: a fresh P-256 key pair.
    class Pairing internal constructor(private val pair: KeyPair) {
        // X ‖ Y, 32 bytes each, big-endian: what PAIR sends.
        val public: ByteArray = encode(pair.public as ECPublicKey)

        // The long-term key, from the PC's public key; null if it isn't a point on the curve.
        fun agree(pcPublic: ByteArray): ByteArray? = runCatching {
            val agreement = KeyAgreement.getInstance("ECDH").apply {
                init(pair.private)
                doPhase(decode(pcPublic), true)
            }
            val shared = MessageDigest.getInstance("SHA-256").digest(agreement.generateSecret())
            hmac(shared, "sonora-v1-pair".toByteArray(Charsets.US_ASCII) + public + pcPublic)
        }.getOrNull()
    }

    fun pairing(): Pairing = Pairing(KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair())

    // Tests only: a pairing from a known private key and its public key.
    internal fun pairing(private: ByteArray, public: ByteArray): Pairing {
        val privateKey = KeyFactory.getInstance("EC").generatePrivate(ECPrivateKeySpec(BigInteger(1, private), curve)) as ECPrivateKey
        return Pairing(KeyPair(decode(public), privateKey))
    }

    // The number both screens show, "482 913": the same derivation as a USB pairing's.
    fun code(longTermKey: ByteArray): String = PairingLink.numberFor(longTermKey)

    // JOIN's proof: HMAC-SHA256(L, "sonora-v1-wifi" ‖ challenge ‖ nonce ‖ port), lower-case hex.
    fun joinProof(longTermKey: ByteArray, challenge: String, nonce: String, port: Int): String =
        hmac(longTermKey, "sonora-v1-wifi$challenge$nonce$port".toByteArray(Charsets.US_ASCII)).joinToString("") { "%02x".format(it) }

    // JOINED's proof from the PC: HMAC-SHA256(L, "sonora-v1-joined" ‖ challenge ‖ nonce). Only the PC
    // this phone paired with can make it.
    fun joinedProof(longTermKey: ByteArray, challenge: String, nonce: String): String =
        hmac(longTermKey, "sonora-v1-joined$challenge$nonce".toByteArray(Charsets.US_ASCII)).joinToString("") { "%02x".format(it) }

    fun sameProof(a: String?, b: String): Boolean =
        a != null && java.security.MessageDigest.isEqual(a.toByteArray(Charsets.US_ASCII), b.toByteArray(Charsets.US_ASCII))

    // The stream's key K: HMAC-SHA256(L, "sonora-v1-stream" ‖ challenge ‖ nonce).
    fun streamKey(longTermKey: ByteArray, challenge: String, nonce: String): ByteArray =
        hmac(longTermKey, "sonora-v1-stream$challenge$nonce".toByteArray(Charsets.US_ASCII))

    fun base64Url(bytes: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)
    fun fromBase64Url(text: String?): ByteArray? = text?.let { runCatching { Base64.getUrlDecoder().decode(it) }.getOrNull() }

    private fun encode(key: ECPublicKey): ByteArray = fixed(key.w.affineX) + fixed(key.w.affineY)

    private fun decode(public: ByteArray): ECPublicKey {
        require(public.size == 64)
        val point = ECPoint(BigInteger(1, public.copyOfRange(0, 32)), BigInteger(1, public.copyOfRange(32, 64)))
        return KeyFactory.getInstance("EC").generatePublic(ECPublicKeySpec(point, curve)) as ECPublicKey
    }

    // A coordinate as exactly 32 big-endian bytes (BigInteger adds a sign byte or drops leading zeros).
    private fun fixed(value: BigInteger): ByteArray {
        val bytes = value.toByteArray()
        return when {
            bytes.size == 32 -> bytes
            bytes.size > 32 -> bytes.copyOfRange(bytes.size - 32, bytes.size)
            else -> ByteArray(32 - bytes.size) + bytes
        }
    }

    private fun hmac(key: ByteArray, data: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }.doFinal(data)
}
