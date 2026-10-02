package app.sonora.receiver

import android.content.Context
import app.sonora.receiver.net.WifiTrust
import java.security.SecureRandom

// This phone's ID for PCs on Wi‑Fi, and the long-term keys of the PCs it has paired with, in the
// app's private storage. A PC that forgets this phone just asks for Allow again.
object Trust {
    private fun prefs(context: Context) = context.getSharedPreferences("trust", Context.MODE_PRIVATE)

    fun phoneId(context: Context): String {
        val prefs = prefs(context)
        prefs.getString("phone", null)?.takeIf { it.length == 16 }?.let { return it }
        val id = ByteArray(8).also { SecureRandom().nextBytes(it) }.joinToString("") { "%02x".format(it) }
        prefs.edit().putString("phone", id).apply()
        return id
    }

    fun key(context: Context, pcId: String): ByteArray? =
        WifiTrust.fromBase64Url(prefs(context).getString("pc.$pcId", null))?.takeIf { it.size == 32 }

    fun remember(context: Context, pcId: String, key: ByteArray) =
        prefs(context).edit().putString("pc.$pcId", WifiTrust.base64Url(key)).apply()

    fun forget(context: Context, pcId: String) = prefs(context).edit().remove("pc.$pcId").apply()
}
