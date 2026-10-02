package app.sonora.receiver.net

import java.net.URI
import java.net.URLDecoder
import java.net.URLEncoder
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

    // A PC's answer to the Wi‑Fi query (WifiFinder): "SONORA! v=1&name=…&id=<16 hex>&port=<n>".
    data class Answer(val id: String, val name: String, val port: Int)

    private val pcId = Regex("^[0-9a-f]{16}$")

    fun answer(line: String): Answer? {
        if (!line.startsWith("SONORA! ")) return null
        val f = fields(line)
        if (f["v"] != "1") return null
        val id = f["id"]?.takeIf { pcId.matches(it) } ?: return null
        val port = f["port"]?.toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
        return Answer(id, f["name"]?.takeIf { it.isNotBlank() }?.take(60) ?: "PC", port)
    }

    // "LINK request=…&url=…" (or "&none=1"): the request it answers, and the link if the PC had one
    // that's safe to open.
    fun link(line: String): Pair<String, String?>? {
        if (!line.startsWith("LINK ")) return null
        val f = fields(line)
        val request = f["request"] ?: return null
        return request to openable(f["url"])
    }

    // Only ordinary web links are opened, whatever arrives: http or https, with a host, not huge.
    fun openable(url: String?): String? {
        if (url.isNullOrBlank() || url.length > 2048) return null
        val uri = runCatching { URI(url) }.getOrNull() ?: return null
        val scheme = uri.scheme?.lowercase()
        if ((scheme != "http" && scheme != "https") || uri.host.isNullOrBlank()) return null
        return url
    }

    // No link (an app rather than a browser, or a tab in the background): a search for the track
    // where it most likely is, Spotify for Spotify and YouTube for everything else.
    fun search(media: Media): String {
        val words = listOf(media.title, media.artist).filter { it.isNotBlank() }.joinToString(" ")
        val query = URLEncoder.encode(words, "UTF-8").replace("+", "%20")
        return if (media.app.equals("Spotify", ignoreCase = true)) "https://open.spotify.com/search/$query"
        else "https://www.youtube.com/results?search_query=$query"
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
