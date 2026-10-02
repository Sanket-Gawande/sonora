package app.sonora.receiver.net

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Log
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException
import java.nio.ByteBuffer
import kotlin.concurrent.thread

// PCs with Sonora open on a local network this phone shares with them: the Wi‑Fi it's on, and the
// networks it provides itself (its hotspot, USB tethering). Every 2 s while the app is on screen, a
// UDP broadcast to port 47212 asks "SONORA? v=1" on each, and each PC answers with its name, ID and
// port; one not heard from for 7 s drops off the list. The Wi‑Fi socket is bound to the Wi‑Fi
// network, so this works even when mobile data is the phone's default. docs/protocol.md, "Wi‑Fi".
object WifiFinder {
    const val PORT = 47_212
    private const val FORGET_MS = 7_000L
    private const val TAG = "Sonora"
    // Also asked on this multicast group: some routers drop broadcasts between Wi‑Fi devices but
    // pass multicast to devices that joined the group (Sonora on the PC does).
    private val GROUP: InetAddress = InetAddress.getByName("239.255.47.212")
    // What the last round found, so the log only notes changes.
    private var lastRound = ""

    // `wifi`: found on the Wi‑Fi network (its connection is bound to it); otherwise on this phone's
    // own hotspot or tethering, which any socket reaches.
    data class Pc(val id: String, val name: String, val address: String, val port: Int, val seen: Long, val wifi: Boolean)

    // Main thread only; sorted by name, one entry per PC ID.
    var pcs by mutableStateOf<List<Pc>>(emptyList())
        private set

    private val main = Handler(Looper.getMainLooper())
    private val lock = Object()
    @Volatile private var wanted = false
    private var worker: Thread? = null
    private var app: Context? = null

    fun start(context: Context) {
        app = context.applicationContext
        synchronized(lock) { wanted = true; lock.notifyAll() }
        if (worker == null) worker = thread(name = "Sonora Wi-Fi finder", isDaemon = true) { loop() }
    }

    fun stop() = synchronized(lock) { wanted = false }

    // The Wi‑Fi network, if the phone is on one.
    fun network(context: Context): Network? {
        val connectivity = context.getSystemService(ConnectivityManager::class.java) ?: return null
        @Suppress("DEPRECATION")
        return connectivity.allNetworks.firstOrNull {
            connectivity.getNetworkCapabilities(it)?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
        }
    }

    private class Probe(val socket: DatagramSocket, val wifi: Boolean)

    private fun loop() {
        val buffer = ByteArray(512)
        val query = "SONORA? v=1".toByteArray(Charsets.UTF_8)
        while (true) {
            synchronized(lock) { while (!wanted) lock.wait() }
            val context = app ?: continue
            val probes = open(context)
            val answered = sortedSetOf<String>()
            val sent = mutableListOf<String>()
            try {
                for (probe in probes) for (address in targets(context, probe)) {
                    runCatching { probe.socket.send(DatagramPacket(query, query.size, address, PORT)) }
                        .onSuccess { sent += (if (probe.wifi) "wifi:" else "local:") + address.hostAddress }
                        .onFailure { Log.w(TAG, "discovery: can't send to ${address.hostAddress}: $it") }
                }
                val until = SystemClock.elapsedRealtime() + 2000
                while (wanted && SystemClock.elapsedRealtime() < until) {
                    if (probes.isEmpty()) { synchronized(lock) { lock.wait(until - SystemClock.elapsedRealtime()) }; break }
                    for (probe in probes) {
                        val packet = DatagramPacket(buffer, buffer.size)
                        try { probe.socket.receive(packet) } catch (_: SocketTimeoutException) { continue } catch (_: IOException) { continue }
                        val answer = Presence.answer(String(buffer, 0, packet.length, Charsets.UTF_8)) ?: continue
                        val address = packet.address.hostAddress ?: continue
                        answered += "${answer.name}@$address"
                        seen(Pc(answer.id, answer.name, address, answer.port, SystemClock.elapsedRealtime(), probe.wifi))
                    }
                }
            } finally {
                probes.forEach { runCatching { it.socket.close() } }
            }
            val round = "asked ${sent.joinToString()}; answered by ${answered.joinToString().ifEmpty { "nobody" }}"
            if (round != lastRound) { lastRound = round; Log.i(TAG, "discovery: $round") }
            expire()
        }
    }

    // One socket for the Wi‑Fi network, bound to it, and one for each network this phone provides
    // (an interface with a broadcast address that isn't the Wi‑Fi's or mobile data's).
    private fun open(context: Context): List<Probe> {
        val probes = mutableListOf<Probe>()
        val network = network(context)
        val wifiInterface = network?.let { context.getSystemService(ConnectivityManager::class.java)?.getLinkProperties(it)?.interfaceName }
        if (network != null) runCatching {
            probes += Probe(DatagramSocket().apply { network.bindSocket(this); broadcast = true; soTimeout = 60 }, wifi = true)
        }.onFailure { Log.w(TAG, "discovery: can't open a socket on the Wi‑Fi: $it") }
        val interfaces = runCatching { NetworkInterface.getNetworkInterfaces()?.toList().orEmpty() }.getOrDefault(emptyList())
        if (network == null) Log.i(TAG, "discovery: Android names no Wi‑Fi network; using wlan0 if it's up")
        for (nic in interfaces) {
            if (nic.name == wifiInterface || !runCatching { nic.isUp && !nic.isLoopback }.getOrDefault(false)) continue
            // Without a Wi‑Fi network from Android, wlan0 (its usual Wi‑Fi interface) still counts as Wi‑Fi.
            val wifi = network == null && nic.name == "wlan0"
            for (link in nic.interfaceAddresses) {
                val ip = link.address as? Inet4Address ?: continue
                if (link.broadcast == null) continue
                runCatching { probes += Probe(DatagramSocket(InetSocketAddress(ip, 0)).apply { broadcast = true; soTimeout = 60 }, wifi = wifi) }
            }
        }
        return probes
    }

    // Where a probe's query goes: its network's own broadcast address, and the all-ones one.
    private fun targets(context: Context, probe: Probe): List<InetAddress> {
        val addresses = mutableListOf<InetAddress>()
        if (probe.wifi) {
            val network = network(context)
            val links = network?.let { context.getSystemService(ConnectivityManager::class.java)?.getLinkProperties(it)?.linkAddresses }.orEmpty()
            for (link in links) {
                val ip = link.address as? Inet4Address ?: continue
                addresses += broadcastOf(ip, link.prefixLength)
            }
        }
        if (!probe.wifi || addresses.isEmpty()) {
            val local = probe.socket.localAddress
            NetworkInterface.getByInetAddress(local)?.interfaceAddresses?.firstOrNull { it.address == local }?.broadcast?.let { addresses += it }
        }
        addresses += InetAddress.getByName("255.255.255.255")
        addresses += GROUP
        return addresses.distinct()
    }

    private fun broadcastOf(ip: Inet4Address, prefix: Int): InetAddress {
        val bits = prefix.coerceIn(0, 32)
        val mask = if (bits == 0) 0 else -1 shl (32 - bits)
        val broadcast = ByteBuffer.wrap(ip.address).int or mask.inv()
        return InetAddress.getByAddress(ByteBuffer.allocate(4).putInt(broadcast).array())
    }

    // A PC that answers on several networks (Wi‑Fi and this phone's tethering) keeps its Wi‑Fi address.
    private fun seen(pc: Pc) = main.post {
        val known = pcs.firstOrNull { it.id == pc.id }
        val next = if (known != null && known.wifi && !pc.wifi && pc.seen - known.seen < FORGET_MS) known.copy(seen = pc.seen) else pc
        pcs = (pcs.filter { it.id != pc.id } + next).sortedBy { it.name.lowercase() }
    }

    private fun expire() = main.post {
        val now = SystemClock.elapsedRealtime()
        val fresh = pcs.filter { now - it.seen < FORGET_MS }
        if (fresh.size != pcs.size) pcs = fresh
    }
}
