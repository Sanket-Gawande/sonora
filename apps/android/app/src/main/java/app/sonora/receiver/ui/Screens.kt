package app.sonora.receiver.ui

import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.media.AudioDeviceCallback
import android.media.AudioDeviceInfo
import android.media.AudioManager
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import app.sonora.receiver.Outputs
import app.sonora.receiver.ReceiverModel
import app.sonora.receiver.ReceiverService
import app.sonora.receiver.Screen
import app.sonora.receiver.Trust
import app.sonora.receiver.UsbStatus
import app.sonora.receiver.net.PcFinder
import app.sonora.receiver.net.WifiFinder
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

@Composable
fun SonoraApp(receiver: ReceiverModel) {
    Box(Modifier.fillMaxSize().background(Sonora.Ink)) {
        if (receiver.screen == Screen.Receiving) {
            // The player draws edge to edge (its colour comes from the artwork).
            PlayerScreen(receiver)
        } else {
            Box(
                Modifier
                    .fillMaxSize()
                    .systemBarsPadding()
                    .padding(horizontal = 20.dp, vertical = 16.dp),
            ) {
                when (receiver.screen) {
                    Screen.Confirm -> ConfirmScreen(receiver)
                    Screen.PairWifi -> WifiPairScreen(receiver)
                    else -> NearbyScreen(receiver)
                }
            }
        }
    }
}

// ---------- shared pieces ----------

@Composable
internal fun Glyph(icon: ImageVector, size: Dp, tint: Color = Sonora.Text) =
    Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.size(size))

@Composable
private fun PrimaryButton(label: String, icon: ImageVector? = null, busy: Boolean = false, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .height(56.dp)
            .clip(RoundedCornerShape(28.dp))
            .background(if (busy) Sonora.Text.copy(alpha = 0.72f) else Sonora.Text)
            .clickable(enabled = !busy, role = Role.Button, onClickLabel = label, onClick = onClick),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (busy) {
            CircularProgressIndicator(Modifier.size(18.dp), color = Sonora.Ink, strokeWidth = 2.dp)
            Spacer(Modifier.width(12.dp))
        } else if (icon != null) {
            Glyph(icon, 20.dp, Sonora.Ink)
            Spacer(Modifier.width(10.dp))
        }
        Text(label, style = text(16, FontWeight.SemiBold, Sonora.Ink))
    }
}

@Composable
private fun QuietButton(label: String, onClick: () -> Unit) {
    Box(
        Modifier
            .fillMaxWidth()
            .height(48.dp)
            .clip(RoundedCornerShape(24.dp))
            .clickable(role = Role.Button, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) { Text(label, style = text(15, FontWeight.Medium, Sonora.Text2)) }
}

@Composable
internal fun RoundButton(icon: ImageVector, label: String, size: Dp, primary: Boolean = false, enabled: Boolean = true, onClick: () -> Unit) {
    Box(
        Modifier
            .size(size)
            .alpha(if (enabled) 1f else 0.35f)
            .clip(CircleShape)
            .background(if (primary) Sonora.Text else Sonora.Card2)
            .clickable(enabled = enabled, role = Role.Button, onClickLabel = label, onClick = onClick)
            .semantics { contentDescription = label },
        contentAlignment = Alignment.Center,
    ) { Glyph(icon, if (primary) 28.dp else 22.dp, if (primary) Sonora.Ink else Sonora.Text) }
}

@Composable
private fun Card(modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    Column(
        modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(22.dp))
            .background(Sonora.Card)
            .padding(16.dp),
    ) { content() }
}

@Composable
private fun Label(text: String) = Text(text, style = text(12, FontWeight.SemiBold, Sonora.Text3, 0.06f))

@Composable
private fun DeviceTile(icon: ImageVector, size: Dp = 44.dp) {
    Box(Modifier.size(size).clip(RoundedCornerShape(size * 0.32f)).background(Sonora.Card2), contentAlignment = Alignment.Center) {
        Glyph(icon, size / 2)
    }
}

@Composable
private fun Chip(label: String, live: Boolean) {
    Row(
        Modifier
            .clip(RoundedCornerShape(12.dp))
            .background(if (live) Sonora.Live.copy(alpha = 0.12f) else Sonora.Card2)
            .padding(horizontal = 10.dp, vertical = 5.dp)
            .semantics { liveRegion = LiveRegionMode.Polite },
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(6.dp).clip(CircleShape).background(if (live) Sonora.Live else Sonora.Text3))
        Spacer(Modifier.width(6.dp))
        Text(label, style = text(12, FontWeight.SemiBold, if (live) Sonora.Live else Sonora.Text2))
    }
}

// ---------- home ----------

@Composable
private fun NearbyScreen(receiver: ReceiverModel) {
    val context = LocalContext.current
    val usb = rememberUsbStatus()
    // PCs with Sonora open on this Wi‑Fi (or this phone's hotspot).
    val wifi = WifiFinder.pcs
    // The PC reached through adb: over the cable, or over wireless debugging when there's no cable.
    // Without a cable, a PC that's also on the Wi‑Fi is better reached directly, so it's offered
    // there only. (While the channel itself is on Wi‑Fi, there's no adb PC to show.)
    val pc = PcFinder.pc?.takeIf { found -> !PcFinder.overWifi && (usb.cable || wifi.none { it.id == found.id }) }
    val active = receiver.active
    Column(Modifier.fillMaxSize()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Glyph(Icons.Logo, 22.dp, Sonora.Signal)
            Spacer(Modifier.width(10.dp))
            Text("Sonora", style = text(17, FontWeight.SemiBold, tracking = -0.01f))
        }
        Spacer(Modifier.height(28.dp))
        Text(
            "Play your PC\non this phone",
            style = text(30, FontWeight.SemiBold, tracking = -0.03f),
            modifier = Modifier.semantics { heading() },
        )
        Spacer(Modifier.height(10.dp))
        Text(
            when {
                active != null -> "Your PC’s audio plays here, even when you leave the app."
                pc != null -> "Sonora is open on ${pc.name}. Connect to hear it on this phone."
                wifi.isNotEmpty() -> "Sonora is open on this Wi‑Fi. Connect to hear your PC on this phone."
                else -> "Open Sonora on your PC. It shows up here by itself, on this Wi‑Fi or over a USB cable."
            },
            style = text(15, color = Sonora.Text2).copy(lineHeight = text(15).fontSize * 1.45f),
        )
        Spacer(Modifier.height(24.dp))
        Column(
            Modifier.weight(1f).verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            if (active != null) {
                ConnectedCard(active.name, active.host == ReceiverService.LOOPBACK) { receiver.openActive() }
            } else {
                // Wi‑Fi first; a PC on the cable as well is one line under it.
                if (pc != null && wifi.isEmpty()) AvailableCard(pc, cable = usb.cable, connecting = receiver.connecting, problem = receiver.connectProblem ?: pc.problem)
                if (wifi.isNotEmpty()) WifiCard(wifi, receiver, buttons = wifi.size > 1)
                if (pc != null && wifi.isNotEmpty()) UsbLine(receiver.connecting, receiver.connectProblem ?: pc.problem) { receiver.connect() }
                if (pc == null && wifi.isEmpty()) FindingCard(usb)
            }
        }
        Spacer(Modifier.height(16.dp))
        val connectingWifi = receiver.wifiTarget != null
        when {
            active != null -> PrimaryButton("Open player") { receiver.openActive() }
            wifi.size == 1 -> PrimaryButton(if (connectingWifi) "Connecting…" else "Connect over Wi‑Fi", Icons.Pc, busy = connectingWifi) { receiver.connectWifi(context, wifi[0]) }
            pc != null && wifi.isEmpty() -> PrimaryButton(if (receiver.connecting) "Connecting…" else "Connect", Icons.Pc, busy = receiver.connecting) { receiver.connect() }
            wifi.isEmpty() && !usb.debugging -> PrimaryButton("Open developer options") { openDeveloperOptions(context) }
        }
    }
}

private fun openDeveloperOptions(context: Context) {
    // Where USB debugging lives; some phones hide it until Developer options is enabled.
    runCatching { context.startActivity(Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS)) }
        .onFailure { context.startActivity(Intent(Settings.ACTION_DEVICE_INFO_SETTINGS)) }
}

// Read again every 1.5 s while the home screen shows, so turning debugging on or plugging in
// moves the list along without a tap.
@Composable
private fun rememberUsbStatus(): UsbStatus {
    val context = LocalContext.current
    var status by remember { mutableStateOf(UsbStatus.read(context)) }
    LaunchedEffect(Unit) {
        while (true) {
            delay(1500)
            status = UsbStatus.read(context)
        }
    }
    return status
}

// A PC found through adb, with Sonora open and ready: over the cable, or over wireless debugging.
@Composable
private fun AvailableCard(pc: PcFinder.Pc, cable: Boolean, connecting: Boolean, problem: String?) {
    Card {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Label("AVAILABLE")
            Spacer(Modifier.weight(1f))
            Chip(if (cable) "USB" else "ADB", live = true)
        }
        Spacer(Modifier.height(12.dp))
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.semantics(mergeDescendants = true) {}) {
            DeviceTile(Icons.Pc)
            Spacer(Modifier.width(14.dp))
            Column(Modifier.weight(1f)) {
                Text(pc.name, style = text(16, FontWeight.SemiBold), maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(
                    when {
                        connecting -> "Starting the stream…"
                        pc.busy -> "Playing to another phone. Connect moves it here."
                        cable -> "Windows · connected by USB"
                        else -> "Windows · through wireless debugging"
                    },
                    style = text(13, color = Sonora.Text2),
                    modifier = Modifier.padding(top = 2.dp),
                )
            }
        }
        if (problem != null && !connecting) {
            Spacer(Modifier.height(12.dp))
            Text(problem, style = text(13, color = Sonora.Warn), modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite })
        }
    }
}

// PCs with Sonora open on this Wi‑Fi. With more than one (or a PC on the cable too), each row has
// its own Connect; otherwise the screen's main button connects.
@Composable
private fun WifiCard(pcs: List<WifiFinder.Pc>, receiver: ReceiverModel, buttons: Boolean) {
    val context = LocalContext.current
    val busy = receiver.wifiTarget != null
    Card {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Label("ON THIS WI‑FI")
            Spacer(Modifier.weight(1f))
            Chip("Wi‑Fi", live = true)
        }
        pcs.forEach { pc ->
            val connecting = receiver.wifiTarget?.id == pc.id
            // Read again after a pairing (the target clears when it's done).
            val paired = remember(pc.id, receiver.wifiTarget) { Trust.key(context, pc.id) != null }
            val problem = receiver.wifiProblem?.takeIf { it.first == pc.id }?.second
            Spacer(Modifier.height(12.dp))
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.semantics(mergeDescendants = true) {},
            ) {
                DeviceTile(Icons.Pc)
                Spacer(Modifier.width(14.dp))
                Column(Modifier.weight(1f)) {
                    Text(pc.name, style = text(16, FontWeight.SemiBold), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(
                        when {
                            connecting -> "Connecting…"
                            !paired -> "Windows · allow this phone on the PC once"
                            pc.wifi -> "Windows · on this Wi‑Fi"
                            else -> "Windows · on this phone’s hotspot"
                        },
                        style = text(13, color = Sonora.Text2),
                        modifier = Modifier.padding(top = 2.dp),
                    )
                }
                if (buttons) {
                    Spacer(Modifier.width(10.dp))
                    if (connecting) CircularProgressIndicator(Modifier.size(20.dp), color = Sonora.Text2, strokeWidth = 2.dp)
                    else SmallButton("Connect", enabled = !busy) { receiver.connectWifi(context, pc) }
                }
            }
            if (problem != null && !connecting) {
                Spacer(Modifier.height(10.dp))
                Text(problem, style = text(13, color = Sonora.Warn), modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite })
            }
        }
    }
}

// The PC is on the cable too: USB as the quieter choice under the Wi‑Fi card.
@Composable
private fun UsbLine(connecting: Boolean, problem: String?, connect: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .border(1.dp, Sonora.Line, RoundedCornerShape(18.dp))
            .padding(start = 16.dp, end = 6.dp, top = 4.dp, bottom = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Glyph(Icons.Phone, 18.dp, Sonora.Text3)
        Spacer(Modifier.width(12.dp))
        Text(
            if (connecting) "Connecting over USB…" else problem ?: "Also reachable over USB",
            style = text(13, color = if (problem != null && !connecting) Sonora.Warn else Sonora.Text2),
            modifier = Modifier.weight(1f),
        )
        Box(
            Modifier
                .height(44.dp)
                .clip(RoundedCornerShape(22.dp))
                .clickable(enabled = !connecting, role = Role.Button, onClickLabel = "Connect over USB", onClick = connect)
                .padding(horizontal = 12.dp),
            contentAlignment = Alignment.Center,
        ) { Text("Use USB", style = text(13, FontWeight.SemiBold, Sonora.Signal)) }
    }
}

@Composable
private fun SmallButton(label: String, enabled: Boolean = true, onClick: () -> Unit) {
    Box(
        Modifier
            .height(36.dp)
            .clip(RoundedCornerShape(18.dp))
            .background(if (enabled) Sonora.Text else Sonora.Text.copy(alpha = 0.4f))
            .clickable(enabled = enabled, role = Role.Button, onClickLabel = label, onClick = onClick)
            .padding(horizontal = 16.dp),
        contentAlignment = Alignment.Center,
    ) { Text(label, style = text(14, FontWeight.SemiBold, Sonora.Ink)) }
}

// No PC yet: what the phone can check for itself, ticked off as it happens.
@Composable
private fun FindingCard(usb: UsbStatus) {
    val pulse by rememberInfiniteTransition(label = "finding").animateFloat(
        initialValue = 0.25f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(tween(900), RepeatMode.Reverse),
        label = "pulse",
    )
    Card {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Label("LOOKING FOR YOUR PC")
            Spacer(Modifier.weight(1f))
            Box(Modifier.size(8.dp).alpha(pulse).clip(CircleShape).background(Sonora.Signal))
        }
        Spacer(Modifier.height(16.dp))
        Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Step(
                1, done = usb.debugging, next = !usb.debugging,
                title = if (usb.debugging) "USB debugging is on" else "Turn on USB debugging",
                detail = "It’s in Developer options. No Developer options? Tap Build number 7 times in About phone.",
            )
            Step(
                2, done = usb.cable, next = usb.debugging && !usb.cable,
                title = if (usb.cable) "Plugged into a computer" else "Plug this phone into your PC",
                detail = "Use a cable that carries data, and tap Allow when the phone asks about USB debugging.",
            )
            Step(
                3, done = false, next = usb.debugging && usb.cable,
                title = "Open Sonora on your PC",
                detail = "It finds this phone by itself. Still nothing? Unplug and plug in again, then tap Allow on the phone.",
            )
        }
    }
}

@Composable
private fun Step(number: Int, done: Boolean, next: Boolean, title: String, detail: String) {
    Row(
        verticalAlignment = Alignment.Top,
        modifier = Modifier.semantics(mergeDescendants = true) {
            stateDescription = if (done) "Done" else if (next) "Next step" else "Not yet"
        },
    ) {
        Box(
            Modifier
                .size(26.dp)
                .clip(CircleShape)
                .background(if (done) Sonora.Live.copy(alpha = 0.16f) else if (next) Sonora.Signal else Sonora.Card2),
            contentAlignment = Alignment.Center,
        ) {
            if (done) Glyph(Icons.Check, 16.dp, Sonora.Live)
            else Text("$number", style = text(12, FontWeight.SemiBold, if (next) Sonora.Ink else Sonora.Text3))
        }
        Spacer(Modifier.width(12.dp))
        Column(Modifier.padding(top = 3.dp)) {
            Text(title, style = text(15, if (next) FontWeight.SemiBold else FontWeight.Medium, if (done) Sonora.Text2 else if (next) Sonora.Text else Sonora.Text3))
            if (next) {
                Text(
                    detail,
                    style = text(13, color = Sonora.Text2).copy(lineHeight = text(13).fontSize * 1.4f),
                    modifier = Modifier.padding(top = 4.dp),
                )
            }
        }
    }
}

// The stream that keeps playing while you're elsewhere in the app. Tap to open the player.
@Composable
private fun ConnectedCard(name: String, usb: Boolean, open: () -> Unit) {
    var connected by remember { mutableStateOf(ReceiverService.current?.stats()?.connected == true) }
    var output by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(Unit) {
        while (true) {
            val receiver = ReceiverService.current
            connected = receiver?.stats()?.connected == true
            output = receiver?.routed?.let(Outputs::name)
            delay(500)
        }
    }
    Column(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(22.dp))
            .background(Sonora.Card)
            .clickable(role = Role.Button, onClickLabel = "Open player", onClick = open)
            .padding(16.dp),
    ) {
        Label("CONNECTED")
        Spacer(Modifier.height(12.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            DeviceTile(Icons.Pc)
            Spacer(Modifier.width(14.dp))
            Column(Modifier.weight(1f)) {
                Text(name, style = text(16, FontWeight.SemiBold), maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(
                    (if (usb) "USB" else "Wi‑Fi") + (output?.let { " · $it" } ?: ""),
                    style = text(13, color = Sonora.Text2),
                    modifier = Modifier.padding(top = 2.dp),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            Spacer(Modifier.width(8.dp))
            Chip(if (connected) "Streaming" else "Connecting", connected)
        }
        val (media, art) = rememberSteadyMedia(PcFinder.media, PcFinder.artwork)
        if (media != null) {
            Spacer(Modifier.height(14.dp))
            Box(Modifier.fillMaxWidth().height(1.dp).background(Sonora.Line))
            Spacer(Modifier.height(14.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                ArtTile(art, 40.dp)
                Spacer(Modifier.width(12.dp))
                Column(Modifier.weight(1f)) {
                    Text(media.title.ifBlank { "Unknown track" }, style = text(14, FontWeight.SemiBold), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    val detail = listOf(media.artist, media.app).filter { it.isNotBlank() }.joinToString(" · ")
                    if (detail.isNotEmpty()) Text(detail, style = text(12, color = Sonora.Text2), maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                Spacer(Modifier.width(10.dp))
                RoundButton(if (media.playing) Icons.Pause else Icons.Play, if (media.playing) "Pause" else "Play", 44.dp, primary = true, enabled = media.canToggle) {
                    PcFinder.control("toggle")
                }
            }
        }
    }
}

// ---------- confirm pairing ----------

@Composable
private fun ConfirmScreen(receiver: ReceiverModel) {
    val context = LocalContext.current
    Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally) {
        Row(Modifier.fillMaxWidth()) {
            Box(
                Modifier
                    .size(44.dp)
                    .clip(CircleShape)
                    .clickable(role = Role.Button, onClickLabel = "Back") { receiver.back() }
                    .semantics { contentDescription = "Back" },
                contentAlignment = Alignment.Center,
            ) { Glyph(Icons.Back, 22.dp) }
        }
        Spacer(Modifier.weight(1f))
        Row(verticalAlignment = Alignment.CenterVertically) {
            DeviceTile(Icons.Pc, 64.dp)
            Row(Modifier.padding(horizontal = 14.dp), horizontalArrangement = Arrangement.spacedBy(5.dp)) {
                listOf(0.35f, 0.6f, 1f).forEach { alpha ->
                    Box(Modifier.size(5.dp).clip(CircleShape).background(Sonora.Signal.copy(alpha = alpha)))
                }
            }
            DeviceTile(Icons.Phone, 64.dp)
        }
        Spacer(Modifier.height(22.dp))
        Text("Pair with ${receiver.computer}?", style = text(26, FontWeight.SemiBold, tracking = -0.02f), modifier = Modifier.semantics { heading() })
        Spacer(Modifier.height(10.dp))
        Text(
            "Check that your PC shows the same number. Only pair if it matches.",
            style = text(15, color = Sonora.Text2),
            textAlign = TextAlign.Center,
        )
        Spacer(Modifier.height(22.dp))
        Box(
            Modifier
                .clip(RoundedCornerShape(22.dp))
                .background(Sonora.Card)
                .padding(horizontal = 28.dp, vertical = 18.dp)
                .semantics { contentDescription = "Pairing number ${receiver.pairingCode}" },
        ) {
            Text(receiver.pairingCode, style = text(44, FontWeight.SemiBold, tracking = 0.1f).copy(fontFeatureSettings = "tnum"))
        }
        Spacer(Modifier.height(12.dp))
        Text(if (receiver.overUsb) "Connected by USB" else "On your Wi‑Fi", style = text(13, color = Sonora.Text3))
        Spacer(Modifier.weight(1f))
        PrimaryButton("Numbers match · Pair") { receiver.confirm(context) }
        QuietButton("Cancel") { receiver.back() }
    }
}

// ---------- pair on Wi‑Fi ----------

// The first time with a PC on Wi‑Fi: this phone and the PC show the same number, and the PC's
// notch asks to Allow it. Nothing to tap here but Cancel.
@Composable
private fun WifiPairScreen(receiver: ReceiverModel) {
    val name = receiver.wifiTarget?.name ?: "your PC"
    val code = receiver.wifiCode
    Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally) {
        Row(Modifier.fillMaxWidth()) {
            Box(
                Modifier
                    .size(44.dp)
                    .clip(CircleShape)
                    .clickable(role = Role.Button, onClickLabel = "Cancel") { receiver.back() }
                    .semantics { contentDescription = "Cancel" },
                contentAlignment = Alignment.Center,
            ) { Glyph(Icons.Back, 22.dp) }
        }
        Spacer(Modifier.weight(1f))
        Row(verticalAlignment = Alignment.CenterVertically) {
            DeviceTile(Icons.Pc, 64.dp)
            Row(Modifier.padding(horizontal = 14.dp), horizontalArrangement = Arrangement.spacedBy(5.dp)) {
                listOf(0.35f, 0.6f, 1f).forEach { alpha ->
                    Box(Modifier.size(5.dp).clip(CircleShape).background(Sonora.Signal.copy(alpha = alpha)))
                }
            }
            DeviceTile(Icons.Phone, 64.dp)
        }
        Spacer(Modifier.height(22.dp))
        Text(
            if (code == null) "Asking $name…" else "Allow on $name",
            style = text(26, FontWeight.SemiBold, tracking = -0.02f),
            textAlign = TextAlign.Center,
            modifier = Modifier.semantics { heading() },
        )
        Spacer(Modifier.height(10.dp))
        Text(
            if (code == null) "Sonora on your PC is opening to ask about this phone."
            else "Your PC shows a number too. If it’s the same, click Allow there.",
            style = text(15, color = Sonora.Text2),
            textAlign = TextAlign.Center,
        )
        Spacer(Modifier.height(22.dp))
        Box(
            Modifier
                .clip(RoundedCornerShape(22.dp))
                .background(Sonora.Card)
                .padding(horizontal = 28.dp, vertical = 18.dp)
                .semantics { contentDescription = if (code == null) "Waiting for the number" else "Pairing number $code" },
            contentAlignment = Alignment.Center,
        ) {
            // The number's own size even while waiting, so nothing jumps when it arrives.
            Text(code ?: "000 000", style = text(44, FontWeight.SemiBold, if (code == null) Color.Transparent else Sonora.Text, 0.1f).copy(fontFeatureSettings = "tnum"))
            if (code == null) CircularProgressIndicator(Modifier.size(26.dp), color = Sonora.Text2, strokeWidth = 2.5.dp)
        }
        Spacer(Modifier.height(12.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (code != null) {
                CircularProgressIndicator(Modifier.size(12.dp), color = Sonora.Text3, strokeWidth = 1.5.dp)
                Spacer(Modifier.width(8.dp))
            }
            Text(if (code == null) "On your Wi‑Fi" else "Waiting for Allow on the PC", style = text(13, color = Sonora.Text3))
        }
        Spacer(Modifier.weight(1f))
        Text("Only the first time. After that it connects in one tap.", style = text(13, color = Sonora.Text3), textAlign = TextAlign.Center)
        Spacer(Modifier.height(8.dp))
        QuietButton("Cancel") { receiver.back() }
    }
}

@Composable
private fun ArtTile(art: Bitmap?, size: Dp) {
    val image = remember(art) { art?.asImageBitmap() }
    Box(Modifier.size(size).clip(RoundedCornerShape(size * 0.22f)).background(Sonora.Card2), contentAlignment = Alignment.Center) {
        if (image != null) Image(image, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
        else Glyph(Icons.Music, size * 0.4f, Sonora.Text2)
    }
}

internal fun clock(ms: Long): String {
    val s = ms.coerceAtLeast(0) / 1000
    return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, s % 3600 / 60, s % 60) else "%d:%02d".format(s / 60, s % 60)
}

// The phone's outputs, kept current as headphones come and go. A picked output that disconnects
// hands the stream back to Android's choice.
@Composable
internal fun rememberOutputs(): List<AudioDeviceInfo> {
    val context = LocalContext.current
    var devices by remember { mutableStateOf(Outputs.available(context)) }
    DisposableEffect(Unit) {
        val audio = context.getSystemService(AudioManager::class.java)
        val callback = object : AudioDeviceCallback() {
            override fun onAudioDevicesAdded(added: Array<out AudioDeviceInfo>) { devices = Outputs.available(context) }
            override fun onAudioDevicesRemoved(removed: Array<out AudioDeviceInfo>) {
                devices = Outputs.available(context)
                if (removed.any { it.id == Outputs.chosen }) choose(null)
            }
        }
        audio.registerAudioDeviceCallback(callback, Handler(Looper.getMainLooper()))
        onDispose { audio.unregisterAudioDeviceCallback(callback) }
    }
    return devices
}

private fun choose(device: AudioDeviceInfo?) {
    Outputs.chosen = device?.id
    ReceiverService.current?.preferred = device
}

internal fun outputIcon(device: AudioDeviceInfo?): ImageVector = when (device?.let(Outputs::kind)) {
    Outputs.Kind.Wired, Outputs.Kind.Usb -> Icons.Headphones
    Outputs.Kind.Bluetooth -> Icons.Bluetooth
    else -> Icons.Speaker
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun OutputSheet(devices: List<AudioDeviceInfo>, routed: AudioDeviceInfo?, onDismiss: () -> Unit) {
    val sheet = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val scope = rememberCoroutineScope()
    val chosen = Outputs.chosen?.let { id -> devices.firstOrNull { it.id == id } }
    val pick: (AudioDeviceInfo?) -> Unit = { device ->
        choose(device)
        scope.launch { sheet.hide() }.invokeOnCompletion { onDismiss() }
    }
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheet,
        containerColor = Sonora.Card,
        contentColor = Sonora.Text,
        dragHandle = {
            Box(Modifier.padding(top = 10.dp, bottom = 6.dp).size(36.dp, 4.dp).clip(CircleShape).background(Sonora.Track))
        },
    ) {
        Column(
            Modifier
                .padding(horizontal = 12.dp)
                .padding(bottom = 16.dp)
                .navigationBarsPadding(),
        ) {
            Text("Play on", style = text(20, FontWeight.SemiBold, tracking = -0.01f), modifier = Modifier.padding(horizontal = 8.dp).semantics { heading() })
            Text(
                "Where this phone plays your PC’s audio.",
                style = text(13, color = Sonora.Text2),
                modifier = Modifier.padding(start = 8.dp, end = 8.dp, top = 4.dp, bottom = 12.dp),
            )
            Column(Modifier.selectableGroup(), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                OutputOption(
                    Icons.Auto, "Automatic",
                    "Follows your phone" + (routed?.takeIf { chosen == null }?.let { " · now ${Outputs.name(it)}" } ?: ""),
                    selected = chosen == null,
                ) { pick(null) }
                devices.forEach { device ->
                    OutputOption(outputIcon(device), Outputs.name(device), kindLabel(device), selected = chosen?.id == device.id) { pick(device) }
                }
            }
            // Android can decline a pick (another app may hold that output); say so rather than pretend.
            if (chosen != null && routed != null && Outputs.name(routed) != Outputs.name(chosen)) {
                Text(
                    "Android is playing on ${Outputs.name(routed)} instead.",
                    style = text(13, color = Sonora.Warn),
                    modifier = Modifier.padding(horizontal = 8.dp, vertical = 10.dp),
                )
            }
        }
    }
}

private fun kindLabel(device: AudioDeviceInfo): String? = when (Outputs.kind(device)) {
    Outputs.Kind.Speaker -> "Built in"
    Outputs.Kind.Wired -> "Wired"
    Outputs.Kind.Usb -> "USB"
    Outputs.Kind.Bluetooth -> "Bluetooth"
    Outputs.Kind.Other -> null
}

@Composable
private fun OutputOption(icon: ImageVector, name: String, detail: String?, selected: Boolean, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .background(if (selected) Sonora.Card2 else Color.Transparent)
            .selectable(selected = selected, role = Role.RadioButton, onClick = onClick)
            .padding(horizontal = 10.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier.size(40.dp).clip(CircleShape).background(if (selected) Sonora.Signal.copy(alpha = 0.18f) else Sonora.Card2),
            contentAlignment = Alignment.Center,
        ) { Glyph(icon, 20.dp, if (selected) Sonora.Signal else Sonora.Text) }
        Spacer(Modifier.width(14.dp))
        Column(Modifier.weight(1f)) {
            Text(name, style = text(15, FontWeight.Medium), maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (detail != null) Text(detail, style = text(12, color = Sonora.Text2), maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        if (selected) Glyph(Icons.Check, 20.dp, Sonora.Signal)
    }
}
