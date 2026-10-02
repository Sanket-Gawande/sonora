package app.sonora.receiver.net

import java.net.URLDecoder
import java.util.Base64
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

// Parses the link in the PC's pairing QR code and derives the number the user compares
// (docs/protocol.md). Returns null for anything that isn't a well-formed v1 link.
data class PairingLink(val host: String, val port: Int, val name: String, val secret: ByteArray) {
    val number: String get() = numberFor(secret)

    companion object {
        // "482 913": the first 4 bytes of HMAC-SHA256(key, "sonora-v1-sas") as a big-endian
        // number, mod 10^6. The same for a USB offer's secret and a Wi‑Fi pairing's key.
        fun numberFor(key: ByteArray): String {
            val mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }
                .doFinal("sonora-v1-sas".toByteArray(Charsets.US_ASCII))
            val value = ((mac[0].toLong() and 0xFF) shl 24 or ((mac[1].toLong() and 0xFF) shl 16) or
                ((mac[2].toLong() and 0xFF) shl 8) or (mac[3].toLong() and 0xFF)) % 1_000_000
            val digits = "%06d".format(value)
            return digits.substring(0, 3) + " " + digits.substring(3)
        }

        fun parse(link: String): PairingLink? {
            if (!link.startsWith("sonora://pair?")) return null
            val query = link.substringAfter('?').split('&').mapNotNull {
                val parts = it.split('=', limit = 2)
                if (parts.size == 2) parts[0] to URLDecoder.decode(parts[1], "UTF-8") else null
            }.toMap()
            if (query["v"] != "1") return null
            val secret = runCatching { Base64.getUrlDecoder().decode(query["k"] ?: return null) }.getOrNull() ?: return null
            val port = query["p"]?.toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
            val host = query["h"]?.takeIf { it.isNotBlank() } ?: return null
            if (secret.size != 32) return null
            return PairingLink(host, port, query["n"] ?: host, secret)
        }
    }
}
