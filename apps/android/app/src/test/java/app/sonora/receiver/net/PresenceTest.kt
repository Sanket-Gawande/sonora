package app.sonora.receiver.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.Base64

// Checks the phone's side of the presence channel against the vector Windows writes
// (`Sonora.exe --presence-test`), so both compute the same proofs and read the same lines.
class PresenceTest {
    private val vector: Map<String, String> =
        javaClass.getResource("/presence-vector.txt")!!.readText().lines().filter { '=' in it }
            .associate { it.substringBefore('=') to it.substringAfter('=') }
    private val secret = Base64.getDecoder().decode(vector.getValue("secret"))
    private val challenge = vector.getValue("challenge")

    @Test
    fun proofsMatchWindows() {
        assertEquals(vector["auth"], Presence.proof(secret, Presence.AUTH, challenge))
        assertEquals(vector["end"], Presence.proof(secret, Presence.END, challenge))
    }

    @Test
    fun readsTheMediaLineWindowsSends() {
        val media = Presence.media(vector.getValue("media"), at = 1_000)!!
        assertEquals("Midnight City", media.title)
        assertEquals("M83", media.artist)
        assertEquals("Spotify", media.app)
        assertTrue(media.playing && media.canToggle && media.canPrevious && media.canNext && media.canSeek)
        assertEquals(61_000, media.positionMs)
        assertEquals(243_000, media.durationMs)
        assertEquals("abc", media.artId)
    }

    @Test
    fun readsTheLinkLineWindowsSends() {
        assertEquals("1234abcd" to "https://www.youtube.com/watch?v=abc&t=95s", Presence.link(vector.getValue("link")))
        assertEquals("1234abce" to null, Presence.link("LINK request=1234abce&none=1"))
        assertNull(Presence.link("MEDIA none=1"))
        assertNull(Presence.link("LINK url=https%3A%2F%2Fexample.com"))
    }

    @Test
    fun opensOnlyWebLinks() {
        assertEquals("https://example.com/a", Presence.openable("https://example.com/a"))
        assertEquals("http://example.com/", Presence.openable("http://example.com/"))
        assertNull(Presence.openable("javascript:alert(1)"))
        assertNull(Presence.openable("intent://scan/#Intent;scheme=zxing;end"))
        assertNull(Presence.openable("file:///sdcard/secret.txt"))
        assertNull(Presence.openable("content://contacts/people"))
        assertNull(Presence.openable("https://" + "a".repeat(2100) + ".com"))
        assertNull(Presence.openable(null))
    }

    @Test
    fun searchesWhereTheTrackMostLikelyIs() {
        val spotify = Presence.media("MEDIA title=Midnight%20City&artist=M83&app=Spotify", at = 0)!!
        assertEquals("https://open.spotify.com/search/Midnight%20City%20M83", Presence.search(spotify))
        val player = Presence.media("MEDIA title=Afterglow&artist=Night%20Drive&app=Media%20Player", at = 0)!!
        assertEquals("https://www.youtube.com/results?search_query=Afterglow%20Night%20Drive", Presence.search(player))
    }

    @Test
    fun positionRunsWhilePlayingAndStopsAtTheEnd() {
        val playing = Presence.media("MEDIA title=x&playing=1&position=1000&duration=5000", at = 10_000)!!
        assertEquals(1_000, playing.position(10_000))
        assertEquals(3_500, playing.position(12_500))
        assertEquals(5_000, playing.position(60_000))
        assertEquals(1_000, playing.position(9_000))
        val paused = playing.copy(playing = false)
        assertEquals(1_000, paused.position(60_000))
    }

    @Test
    fun noneAndJunkMeanNothingPlaying() {
        assertNull(Presence.media("MEDIA none=1", 0))
        assertNull(Presence.media("STATE state=streaming&you=1", 0))
        val odd = Presence.media("MEDIA title=a%2Bb%20c&position=-5&duration=abc&seek=1", 0)!!
        assertEquals("a+b c", odd.title)
        assertEquals(0, odd.positionMs)
        assertEquals(0, odd.durationMs)
        assertFalse(odd.canSeek) // nothing to seek within without a length
        assertNull(odd.artId)
    }
}
