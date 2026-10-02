package app.sonora.receiver

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import app.sonora.receiver.net.PairingLink
import app.sonora.receiver.ui.SonoraApp
import app.sonora.receiver.ui.SonoraTheme

class MainActivity : ComponentActivity() {
    private val receiver = ReceiverModel()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Draw behind the status and navigation bars, so they show each screen's own colour (the
        // player's follows the cover); the screens keep their content clear of the bars.
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT),
        )
        takeOffer(intent)
        setContent {
            BackHandler(enabled = receiver.screen != Screen.Nearby) { receiver.back() }
            SonoraTheme { SonoraApp(receiver) }
        }
    }

    override fun onStart() {
        super.onStart()
        receiver.start(this)
    }

    override fun onStop() {
        receiver.stop()
        super.onStop()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        takeOffer(intent)
    }

    // The PC hands over its pairing link as an extra when the phone is on USB, with the request
    // token when this phone asked for it.
    private fun takeOffer(intent: Intent?) {
        val link = intent?.getStringExtra(EXTRA_LINK)?.let(PairingLink::parse) ?: return
        val request = intent.getStringExtra(EXTRA_REQUEST)
        intent.removeExtra(EXTRA_LINK)
        intent.removeExtra(EXTRA_REQUEST)
        receiver.receiveOffer(this, link, request)
    }

    companion object {
        const val EXTRA_LINK = "sonora_link"
        const val EXTRA_REQUEST = "sonora_request"
    }
}
