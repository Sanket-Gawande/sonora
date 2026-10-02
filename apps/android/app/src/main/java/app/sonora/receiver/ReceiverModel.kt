package app.sonora.receiver

import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.Settings
import android.util.Log
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.sonora.receiver.net.PairingLink
import app.sonora.receiver.net.PcFinder
import app.sonora.receiver.net.WifiFinder

enum class Screen { Nearby, Confirm, PairWifi, Receiving }

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

    // Connecting to a PC on Wi‑Fi: which one, and the number to compare while the PC decides
    // (null until the PC sends its half). `wifiProblem`: a PC's ID and why the last try failed.
    var wifiTarget by mutableStateOf<WifiFinder.Pc?>(null)
        private set
    var wifiCode by mutableStateOf<String?>(null)
        private set
    var wifiProblem by mutableStateOf<Pair<String, String>?>(null)
        private set
    private enum class WifiStep { Idle, Reaching, Pairing, Joining }
    private var wifiStep = WifiStep.Idle
    private var repaired = false
    private var app: Context? = null

    private val main = Handler(Looper.getMainLooper())
    private val timeout = Any()
    private val wifiTimeout = Any()
    private val onPc: (PcFinder.Pc) -> Unit = { pc ->
        if (connecting && pc.problem != null) { connecting = false; connectProblem = pc.problem }
        // The new connection to the PC being connected to on Wi‑Fi has greeted us (not a line still
        // arriving on the old channel).
        val target = wifiTarget
        if (wifiStep == WifiStep.Reaching && target != null && pc.host == target.address && pc.id == target.id) proceedWifi()
    }

    val overUsb: Boolean get() = link?.host == ReceiverService.LOOPBACK

    // While the app is on screen it looks for the PC: on the cable, and on the Wi‑Fi.
    fun start(context: Context) {
        app = context.applicationContext
        PcFinder.phoneId = Trust.phoneId(context)
        PcFinder.hold(this)
        PcFinder.listen(onPc)
        WifiFinder.start(context)
    }

    fun stop() {
        WifiFinder.stop()
        PcFinder.unlisten(onPc)
        PcFinder.release(this)
    }

    // Connects to a PC on Wi‑Fi: the channel moves to its address; a PC this phone hasn't paired
    // with shows the number and asks for Allow (both screens show the same number); then the phone
    // joins and the stream starts. A PC that has forgotten this phone just asks again.
    fun connectWifi(context: Context, pc: WifiFinder.Pc) {
        if (wifiStep != WifiStep.Idle) return
        app = context.applicationContext
        connectProblem = null
        wifiProblem = null
        wifiTarget = pc
        wifiCode = null
        repaired = false
        wifiStep = WifiStep.Reaching
        Log.i("Sonora", "wifi: connecting to ${pc.name} at ${pc.address} (${if (pc.wifi) "Wi‑Fi" else "this phone's network"})")
        // Bound to the Wi‑Fi network when it was found there; this phone's own hotspot or tethering needs no binding.
        PcFinder.useWifi(pc.address, if (pc.wifi) WifiFinder.network(context) else null)
        PcFinder.pc?.let { if (it.host == pc.address && it.id == pc.id) proceedWifi() }
        main.removeCallbacksAndMessages(wifiTimeout)
        main.postAtTime({
            if (wifiStep == WifiStep.Reaching) failWifi("Couldn’t reach ${pc.name}. Check that both are on the same Wi‑Fi.")
        }, wifiTimeout, SystemClock.uptimeMillis() + 10_000)
    }

    private fun proceedWifi() {
        val context = app ?: return
        val pc = wifiTarget ?: return
        main.removeCallbacksAndMessages(wifiTimeout)
        val key = Trust.key(context, pc.id)
        Log.i("Sonora", "wifi: reached ${pc.name}; ${if (key != null) "paired, joining" else "not paired yet, pairing"}")
        if (key != null) joinWifi(key) else pairWifi()
    }

    private fun pairWifi() {
        val context = app ?: return
        val pc = wifiTarget ?: return
        wifiStep = WifiStep.Pairing
        wifiCode = null
        current = Screen.PairWifi
        val asked = PcFinder.pair(
            code = { wifiCode = it },
            done = { key ->
                if (wifiStep == WifiStep.Pairing) {
                    if (key == null) failWifi("${pc.name} didn’t allow this phone.")
                    else { Trust.remember(context, pc.id, key); joinWifi(key) }
                }
            },
        )
        if (!asked) failWifi("Lost ${pc.name}. Try again.")
    }

    private fun joinWifi(key: ByteArray) {
        val context = app ?: return
        val pc = wifiTarget ?: return
        wifiStep = WifiStep.Joining
        val asked = PcFinder.join(key, ReceiverService.DEFAULT_PORT) { streamKey ->
            if (wifiStep != WifiStep.Joining) return@join
            when {
                streamKey != null -> {
                    wifiStep = WifiStep.Idle
                    wifiTarget = null
                    wifiCode = null
                    val stream = PairingLink(pc.address, ReceiverService.DEFAULT_PORT, pc.name, streamKey)
                    link = stream
                    computer = pc.name
                    ReceiverService.start(context, stream)
                    current = Screen.Receiving
                }
                // The PC no longer knows this phone (it forgot its Wi‑Fi phones): allow it again.
                !repaired -> { repaired = true; Trust.forget(context, pc.id); pairWifi() }
                else -> failWifi("${pc.name} didn’t accept this phone.")
            }
        }
        if (!asked) failWifi("Lost ${pc.name}. Try again.")
    }

    // Gives up (with why, for the PC's row), and puts the channel back on USB.
    private fun failWifi(why: String?) {
        val pc = wifiTarget
        Log.w("Sonora", "wifi: gave up on ${pc?.name}: ${why ?: "cancelled"}")
        main.removeCallbacksAndMessages(wifiTimeout)
        wifiStep = WifiStep.Idle
        wifiTarget = null
        wifiCode = null
        if (pc != null && why != null) wifiProblem = pc.id to why
        if (current == Screen.PairWifi) current = Screen.Nearby
        if (ReceiverService.activeLink == null) PcFinder.useWifi(null, null)
    }

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
        if (screen == Screen.PairWifi) { failWifi(null); return true }
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
