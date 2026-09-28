package app.sonora.receiver.net

import java.net.URLDecoder
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

// The presence channel's wire format (docs/protocol.md, "Finding the PC over USB"), kept free of
// Android so the unit tests can check it against the PC's own vector.
object Presence {
    const val AUTH = "sonora-v1-auth"
    const val END = "sonora-v1-end"

    // `VERB key=value&key=value`, values URL-encoded.
    fun fields(line: String): Map<String, String> =
        line.substringAfter(' ', "").split('&').mapNotNull {
            val parts = it.split('=', limit = 2)
            if (parts.size == 2) parts[0] to runCatching { URLDecoder.decode(parts[1], "UTF-8") }.getOrDefault("") else null
        }.toMap()

    // HMAC-SHA256(K, label ‖ challenge) in lower-case hex: shows the PC this connection holds the
    // stream's key without sending it.
    fun proof(secret: ByteArray, label: String, challenge: String): String =
        Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(secret, "HmacSHA256")) }
            .doFinal((label + challenge).toByteArray(Charsets.US_ASCII))
            .joinToString("") { "%02x".format(it) }

    // What's playing on the PC, as its MEDIA line says. `at` is when `positionMs` was true on this
    // phone's clock (elapsedRealtime), so the bar keeps moving between updates.
    data class Media(
        val title: String,
        val artist: String,
        val app: String,
        val playing: Boolean,
        val canToggle: Boolean,
        val canPrevious: Boolean,
        val canNext: Boolean,
        val canSeek: Boolean,
        val positionMs: Long,
        val durationMs: Long,
        val artId: String?,
        val at: Long,
    ) {
        fun position(now: Long): Long {
            val p = if (playing) positionMs + (now - at).coerceAtLeast(0) else positionMs
            return if (durationMs > 0) p.coerceIn(0, durationMs) else p.coerceAtLeast(0)
        }
    }

    // Null for "MEDIA none=1" and for anything that isn't a MEDIA line.
    fun media(line: String, at: Long): Media? {
        if (!line.startsWith("MEDIA ")) return null
        val f = fields(line)
        if (f["none"] == "1") return null
        val duration = f["duration"]?.toLongOrNull()?.coerceAtLeast(0) ?: 0
        return Media(
            title = f["title"].orEmpty(),
            artist = f["artist"].orEmpty(),
            app = f["app"].orEmpty(),
            playing = f["playing"] == "1",
            canToggle = f["toggle"] == "1",
            canPrevious = f["previous"] == "1",
            canNext = f["next"] == "1",
            canSeek = f["seek"] == "1" && duration > 0,
            positionMs = f["position"]?.toLongOrNull()?.coerceAtLeast(0) ?: 0,
            durationMs = duration,
            artId = f["art"]?.takeIf { it.isNotEmpty() },
            at = at,
        )
    }
}
