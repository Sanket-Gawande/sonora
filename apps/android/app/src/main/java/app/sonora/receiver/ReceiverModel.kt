package app.sonora.receiver

import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.Settings
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.sonora.receiver.net.PairingLink
import app.sonora.receiver.net.PcFinder

enum class Screen { Nearby, Confirm, Receiving }

// UI state for the receiver. `active` is the stream the playback service is running; it outlives
// any screen, so leaving the player never stops the audio. `link` is what the current screen is
// about: an offer being confirmed, or the active stream.
class ReceiverModel {
    private var current by mutableStateOf(Screen.Nearby)
    // The player shows only while a stream runs: the notification or the PC can end it from outside.
    val screen: Screen get() = if (current == Screen.Receiving && active == null) Screen.Nearby else current
    var computer by mutableStateOf<String?>(null)
        private set
    var pairingCode by mutableStateOf("")
        private set
    var link by mutableStateOf<PairingLink?>(null)
        private set
    val active: PairingLink? get() = ReceiverService.activeLink
    // Asking the PC found over USB to connect, and why the last ask failed.
    var connecting by mutableStateOf(false)
        private set
    var connectProblem by mutableStateOf<String?>(null)
        private set

    private val main = Handler(Looper.getMainLooper())
    private val timeout = Any()
    private val onPc: (PcFinder.Pc) -> Unit = { pc ->
        if (connecting && pc.problem != null) { connecting = false; connectProblem = pc.problem }
    }

    val overUsb: Boolean get() = link?.host == ReceiverService.LOOPBACK

    // While the app is on screen it looks for the PC.
    fun start() { PcFinder.hold(this); PcFinder.listen(onPc) }
    fun stop() { PcFinder.unlisten(onPc); PcFinder.release(this) }

    fun connect() {
        connectProblem = null
        if (!PcFinder.connect()) { connectProblem = "Lost the PC. Check the cable."; return }
        connecting = true
        main.removeCallbacksAndMessages(timeout)
        main.postAtTime({
            if (connecting) { connecting = false; connectProblem = "Your PC didn’t answer. Check that Sonora is open on it." }
        }, timeout, SystemClock.uptimeMillis() + 20_000)
    }

    // `request` comes back from the PC when this phone asked to connect; then there's nothing to
    // confirm. Any other offer shows the number first.
    fun receiveOffer(context: Context, offer: PairingLink, request: String?) {
        link = offer
        computer = offer.name
        pairingCode = offer.number
        if (PcFinder.claim(request)) {
            connecting = false
            main.removeCallbacksAndMessages(timeout)
            confirm(context)
        } else {
            current = Screen.Confirm
        }
    }

    fun confirm(context: Context) {
        val offer = link ?: return
        ReceiverService.start(context, offer)
        current = Screen.Receiving
    }

    fun openActive() {
        val stream = active ?: return
        link = stream
        computer = stream.name
        current = Screen.Receiving
    }

    // Only Disconnect (here or in the notification) ends a stream from the phone.
    fun disconnect(context: Context) {
        ReceiverService.disconnect(context)
        link = null
        current = Screen.Nearby
    }

    fun back(): Boolean {
        if (screen == Screen.Nearby) return false
        link = null
        current = Screen.Nearby
        return true
    }
}

// What the phone itself can tell about a USB connection, so the home screen never asks for a
// setting that's already on.
data class UsbStatus(val debugging: Boolean, val cable: Boolean) {
    companion object {
        fun read(context: Context): UsbStatus {
            val debugging = runCatching { Settings.Global.getInt(context.contentResolver, Settings.Global.ADB_ENABLED, 0) != 0 }.getOrDefault(false)
            // The system's sticky "connected to a USB host" broadcast; charging from a USB port is the fallback.
            val usb: Intent? = runCatching { context.registerReceiver(null, IntentFilter("android.hardware.usb.action.USB_STATE")) }.getOrNull()
            val cable = usb?.getBooleanExtra("connected", false) ?: run {
                val battery = runCatching { context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED)) }.getOrNull()
                battery?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) == BatteryManager.BATTERY_PLUGGED_USB
            }
            return UsbStatus(debugging, cable)
        }
    }
}
