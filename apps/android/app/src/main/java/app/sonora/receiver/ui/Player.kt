package app.sonora.receiver.ui

import android.graphics.Bitmap
import android.media.AudioDeviceInfo
import android.os.SystemClock
import androidx.compose.animation.Crossfade
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.basicMarquee
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.drag
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.ProgressBarRangeInfo
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.progressBarRangeInfo
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.setProgress
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import app.sonora.receiver.Outputs
import app.sonora.receiver.ReceiverModel
import app.sonora.receiver.ReceiverService
import app.sonora.receiver.net.PcFinder
import app.sonora.receiver.net.Presence
import kotlinx.coroutines.delay

// The player, arranged like YouTube Music's: the cover under the header, the track under the cover,
// then space, then the seek bar, one row of controls and the stream's live waveform along the
// bottom, all on the cover's own colour (which the status bar shows too). Every row has a fixed height and the
// cover is sized from what's left, so nothing moves while a track loads. Volume is the phone's own
// buttons: the stream always plays at full level.
@Composable
fun PlayerScreen(receiver: ReceiverModel) {
    val context = LocalContext.current
    // Twice a second is plenty for the output route; it only redraws when it changes.
    var routed by remember { mutableStateOf(ReceiverService.current?.routed) }
    var picking by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        while (true) {
            routed = ReceiverService.current?.routed
            delay(500)
        }
    }
    val outputs = rememberOutputs()
    val (media, art) = rememberSteadyMedia(
        if (receiver.overUsb) PcFinder.media else null,
        if (receiver.overUsb) PcFinder.artwork else null,
    )
    val latency = rememberLatency()
    val computer = receiver.computer ?: "your PC"
    val control: (String, Long?) -> Unit = { action, position -> PcFinder.control(action, position) }

    val target = remember(art) { tintOf(art) }
    val tint by animateColorAsState(target, tween(600), label = "tint")
    Box(
        Modifier
            .fillMaxSize()
            .background(Brush.verticalGradient(0f to tint, 0.62f to Sonora.Ink)),
    ) {
        BoxWithConstraints(
            Modifier
                .fillMaxSize()
                .systemBarsPadding()
                .padding(start = 24.dp, end = 24.dp, top = 4.dp, bottom = 8.dp),
        ) {
            // As wide as the screen allows; on a short screen the cover gives way, never the controls.
            val cover = minOf(maxWidth, maxHeight - FIXED_ROWS).coerceAtLeast(120.dp)
            Column(Modifier.fillMaxSize()) {
                TopBar(computer, latency) { receiver.back() }
                // Spare height on a tall screen: a third above the cover, the rest under the track,
                // so the top half balances instead of leaving one empty band.
                Spacer(Modifier.weight(1f).heightIn(min = 12.dp))
                Artwork(art, Modifier.size(cover).align(Alignment.CenterHorizontally), 24.dp)
                Spacer(Modifier.height(24.dp))
                TrackText(media, computer)
                Spacer(Modifier.weight(2f).heightIn(min = 16.dp))
                SeekBar(media) { control("seek", it) }
                Spacer(Modifier.height(8.dp))
                Controls(
                    media = media,
                    routed = routed,
                    control = control,
                    pickOutput = { picking = true },
                    disconnect = { receiver.disconnect(context) },
                )
                Spacer(Modifier.height(14.dp))
                Waveform(Modifier.fillMaxWidth().height(40.dp))
            }
        }
    }
    if (picking) OutputSheet(outputs, routed) { picking = false }
}

// ⌄ closes the player (the stream keeps playing); the middle says where the sound comes from;
// the right shows the measured latency. Equal side slots keep the title centred.
@Composable
private fun TopBar(computer: String, latency: Int?, close: () -> Unit) {
    Row(Modifier.fillMaxWidth().height(56.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.width(SIDE_SLOT)) {
            Box(
                Modifier
                    .size(44.dp)
                    .clip(CircleShape)
                    .clickable(role = Role.Button, onClickLabel = "Close player", onClick = close)
                    .semantics { contentDescription = "Close player" },
                contentAlignment = Alignment.Center,
            ) { Glyph(Icons.Down, 26.dp) }
        }
        Column(
            Modifier
                .weight(1f)
                .semantics(mergeDescendants = true) { heading() },
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text("PLAYING FROM", style = text(11, FontWeight.SemiBold, Sonora.Text2, 0.08f), maxLines = 1)
            Text(computer, style = text(15, FontWeight.SemiBold), maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        Box(Modifier.width(SIDE_SLOT), contentAlignment = Alignment.CenterEnd) { LatencyPill(latency) }
    }
}

private val SIDE_SLOT = 100.dp

// Everything in the column but the cover: header 56, the least spaces 12 + 16, gap 24, track 58,
// seek 44, gap 8, controls 76, gap 14, waveform 40.
private val FIXED_ROWS = 348.dp

// Title and "artist · app", left-aligned, one line each and always there: a long title scrolls
// instead of wrapping, and a line never appears or disappears as a track loads.
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun TrackText(media: Presence.Media?, computer: String) {
    val title = when {
        media == null -> "Desktop audio"
        media.title.isBlank() -> "Loading…"
        else -> media.title
    }
    val detail = if (media == null) "Playing from $computer"
    else listOf(media.artist, media.app).filter { it.isNotBlank() }.joinToString(" · ").ifBlank { " " }
    Column(Modifier.fillMaxWidth().heightIn(min = 58.dp)) {
        Text(
            title,
            style = text(24, FontWeight.SemiBold, if (media?.title.isNullOrBlank() && media != null) Sonora.Text3 else Sonora.Text, -0.02f),
            maxLines = 1,
            modifier = Modifier.basicMarquee(iterations = Int.MAX_VALUE, initialDelayMillis = 2000),
        )
        Text(
            detail,
            style = text(16, color = Sonora.Text2),
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(top = 2.dp),
        )
    }
}

@Composable
private fun Artwork(art: Bitmap?, modifier: Modifier, radius: Dp) {
    val image = remember(art) { art?.asImageBitmap() }
    Box(modifier.clip(RoundedCornerShape(radius)).background(Sonora.Card2), contentAlignment = Alignment.Center) {
        // A new cover fades in over the old one instead of popping.
        Crossfade(targetState = image, animationSpec = tween(350), label = "artwork") { picture ->
            // Covers can be small (browsers send 120 px): scale them up smoothly, not blocky.
            if (picture != null) Image(picture, contentDescription = null, contentScale = ContentScale.Crop, filterQuality = FilterQuality.High, modifier = Modifier.fillMaxSize())
            else Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { Glyph(Icons.Music, 48.dp, Sonora.Text3) }
        }
    }
}

// A thin bar with the times under it; drag or tap to seek when the playing app allows it.
@Composable
private fun SeekBar(media: Presence.Media?, seek: (Long) -> Unit) {
    val duration = media?.durationMs ?: 0L
    val position = rememberPosition(media)
    var dragging by remember { mutableStateOf<Float?>(null) }
    val fraction = dragging ?: if (duration > 0) (position.toFloat() / duration).coerceIn(0f, 1f) else 0f
    Column(Modifier.fillMaxWidth()) {
        Scrubber(
            fraction = fraction,
            enabled = media?.canSeek == true,
            dragging = dragging != null,
            onDrag = { dragging = it },
            onRelease = {
                dragging?.let { seek((it * duration).toLong()) }
                dragging = null
            },
        )
        Row {
            val shown = if (dragging != null) (fraction * duration).toLong() else position
            Text(if (duration > 0) clock(shown) else "–:––", style = text(12, color = Sonora.Text2).copy(fontFeatureSettings = "tnum"))
            Spacer(Modifier.weight(1f))
            Text(if (duration > 0) clock(duration) else "–:––", style = text(12, color = Sonora.Text2).copy(fontFeatureSettings = "tnum"))
        }
    }
}

@Composable
private fun Scrubber(fraction: Float, enabled: Boolean, dragging: Boolean, onDrag: (Float) -> Unit, onRelease: () -> Unit) {
    var width by remember { mutableIntStateOf(1) }
    val drag by rememberUpdatedState(onDrag)
    val release by rememberUpdatedState(onRelease)
    Box(
        Modifier
            .fillMaxWidth()
            .height(28.dp)
            .onSizeChanged { width = it.width.coerceAtLeast(1) }
            .pointerInput(enabled) {
                if (!enabled) return@pointerInput
                awaitEachGesture {
                    val down = awaitFirstDown()
                    drag((down.position.x / width).coerceIn(0f, 1f))
                    drag(down.id) { change ->
                        drag((change.position.x / width).coerceIn(0f, 1f))
                        change.consume()
                    }
                    release()
                }
            }
            .semantics {
                contentDescription = "Playback position"
                progressBarRangeInfo = ProgressBarRangeInfo(fraction, 0f..1f)
                if (enabled) setProgress { value -> drag(value.coerceIn(0f, 1f)); release(); true }
            },
    ) {
        Canvas(Modifier.fillMaxSize()) {
            val track = 4.dp.toPx()
            val y = size.height / 2
            val x = size.width * fraction
            drawRoundRect(Color.White.copy(alpha = 0.18f), Offset(0f, y - track / 2), Size(size.width, track), CornerRadius(track / 2))
            drawRoundRect(Color.White.copy(alpha = if (enabled) 1f else 0.5f), Offset(0f, y - track / 2), Size(x, track), CornerRadius(track / 2))
            if (enabled) {
                val thumb = (if (dragging) 9.dp else 6.dp).toPx()
                drawCircle(Color.White, radius = thumb, center = Offset(x.coerceIn(thumb, size.width - thumb), y))
            }
        }
    }
}

// Output · previous · play/pause · next · disconnect, spread edge to edge. The two ends are
// matching round buttons; previous and next are plain icons either side of the big play
// button, which sits at the exact centre.
@Composable
private fun Controls(
    media: Presence.Media?,
    routed: AudioDeviceInfo?,
    control: (String, Long?) -> Unit,
    pickOutput: () -> Unit,
    disconnect: () -> Unit,
) {
    val playing = media?.playing == true
    Row(
        Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        EndButton(outputIcon(routed), "Playing on ${routed?.let(Outputs::name) ?: "this phone"}. Choose where to play", pickOutput)
        PlainButton(Icons.Previous, "Previous track", enabled = media?.canPrevious == true) { control("previous", null) }
        RoundButton(
            if (playing) Icons.Pause else Icons.Play,
            if (playing) "Pause" else "Play",
            76.dp,
            primary = true,
            enabled = media?.canToggle == true,
        ) { control("toggle", null) }
        PlainButton(Icons.Next, "Next track", enabled = media?.canNext == true) { control("next", null) }
        EndButton(Icons.Unlink, "Disconnect", disconnect)
    }
}

@Composable
private fun EndButton(icon: ImageVector, label: String, onClick: () -> Unit) {
    Box(
        Modifier
            .size(48.dp)
            .clip(CircleShape)
            .background(Sonora.Card2)
            .clickable(role = Role.Button, onClickLabel = label, onClick = onClick)
            .semantics { contentDescription = label },
        contentAlignment = Alignment.Center,
    ) { Glyph(icon, 20.dp) }
}

@Composable
private fun PlainButton(icon: ImageVector, label: String, enabled: Boolean, onClick: () -> Unit) {
    Box(
        Modifier
            .size(56.dp)
            .clip(CircleShape)
            .clickable(enabled = enabled, role = Role.Button, onClickLabel = label, onClick = onClick)
            .semantics { contentDescription = label },
        contentAlignment = Alignment.Center,
    ) { Glyph(icon, 30.dp, if (enabled) Sonora.Text else Sonora.Text3) }
}

// The stream's live level across the bottom: the newest peak in the middle, older ones rippling
// out to the edges, a flat line in silence (a paused PC, or a quiet passage). Read only while
// drawing, so its 20 frames a second redraw these bars and nothing else.
@Composable
private fun Waveform(modifier: Modifier) {
    val levels = remember { FloatArray(WAVE_HISTORY) }
    var frame by remember { mutableIntStateOf(0) }
    LaunchedEffect(Unit) {
        while (true) {
            val level = ReceiverService.current?.level ?: 0f
            // Rise at once, fall gently, so the bars breathe rather than flicker.
            val eased = maxOf(level, levels[0] * 0.82f)
            for (i in WAVE_HISTORY - 1 downTo 1) levels[i] = levels[i - 1]
            levels[0] = eased
            frame++
            delay(50)
        }
    }
    Canvas(modifier.clearAndSetSemantics {}) {
        frame // redraw on every frame
        val pitch = 6.dp.toPx()
        val bar = 3.dp.toPx()
        val count = (size.width / pitch).toInt().coerceAtLeast(1)
        val left = (size.width - (count - 1) * pitch - bar) / 2
        val centre = (count - 1) / 2f
        for (i in 0 until count) {
            val distance = kotlin.math.abs(i - centre) / centre.coerceAtLeast(1f)
            val level = levels[(distance * (WAVE_HISTORY - 1)).toInt()]
            // Louder in the middle, tapering to the edges; never less than a dot.
            val h = maxOf(bar, size.height * kotlin.math.sqrt(level.coerceIn(0f, 1f)) * (1f - 0.55f * distance))
            drawRoundRect(
                Color.White.copy(alpha = 0.42f),
                topLeft = Offset(left + i * pitch, (size.height - h) / 2),
                size = Size(bar, h),
                cornerRadius = CornerRadius(bar / 2),
            )
        }
    }
}

private const val WAVE_HISTORY = 24

// The measured latency, coloured: a solid dark pill so it reads on any cover colour.
@Composable
private fun LatencyPill(latency: Int?) {
    val (color, word) = latencyLook(latency)
    Row(
        Modifier
            .clip(RoundedCornerShape(14.dp))
            .background(Sonora.Ink)
            .padding(horizontal = 10.dp, vertical = 6.dp)
            .semantics(mergeDescendants = true) {
                contentDescription = if (latency == null) "Latency: measuring" else "Latency $latency milliseconds, $word"
            },
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(7.dp).clip(CircleShape).background(color))
        Spacer(Modifier.width(6.dp))
        Text(latency?.let { "$it ms" } ?: "Measuring", style = text(12, FontWeight.SemiBold, color).copy(fontFeatureSettings = "tnum"), maxLines = 1)
    }
}

// ---------- state ----------

// Between tracks a player often reports nothing for a moment. Keep the last track up for 3 s so
// the screen doesn't flash to "nothing" and back.
@Composable
internal fun rememberSteadyMedia(media: Presence.Media?, art: Bitmap?): Pair<Presence.Media?, Bitmap?> {
    var shown by remember { mutableStateOf(media to art) }
    LaunchedEffect(media, art) {
        if (media != null) {
            shown = media to art
        } else {
            delay(3000)
            shown = null to null
        }
    }
    return shown
}

@Composable
private fun rememberPosition(media: Presence.Media?): Long {
    var now by remember { mutableLongStateOf(SystemClock.elapsedRealtime()) }
    LaunchedEffect(media) {
        now = SystemClock.elapsedRealtime()
        while (media?.playing == true) {
            delay(500)
            now = SystemClock.elapsedRealtime()
        }
    }
    return media?.position(now) ?: 0L
}

// Measured, never estimated: from Windows mixing a sound to this phone's speaker playing it
// (docs/protocol.md, "Latency"). Once a second, the median of the last five readings.
@Composable
private fun rememberLatency(): Int? {
    var latency by remember { mutableStateOf<Int?>(null) }
    LaunchedEffect(Unit) {
        val recent = ArrayDeque<Int>()
        var lastReading = 0L
        while (true) {
            val nowUs = System.nanoTime() / 1000
            val playout = ReceiverService.current?.playout
            // Only a fresh playout counts; a stalled stream leaves an old one behind.
            val ms = playout?.takeIf { kotlin.math.abs(nowUs - it.atUs) < 3_000_000 }?.let { PcFinder.clock.latencyMs(it.frame, it.atUs) }
            if (ms != null) {
                recent.addLast(ms)
                while (recent.size > 5) recent.removeFirst()
                lastReading = nowUs
            } else if (nowUs - lastReading > 5_000_000) {
                recent.clear()
            }
            latency = if (recent.size >= 2) recent.sorted()[recent.size / 2] else null
            delay(1000)
        }
    }
    return latency
}

// Under 60 ms a video on the PC still looks in sync; over 120 ms it doesn't.
private fun latencyLook(ms: Int?): Pair<Color, String?> = when {
    ms == null -> Sonora.Text2 to null
    ms <= 60 -> Sonora.Live to "Good"
    ms <= 120 -> Sonora.Warn to "Fair"
    else -> Sonora.Bad to "High"
}

// The artwork's own colour, deepened to sit behind white text: the most colourful pixels count most.
private fun tintOf(art: Bitmap?): Color {
    if (art == null) return Sonora.Card2
    return runCatching {
        val small = Bitmap.createScaledBitmap(art, 12, 12, true)
        var r = 0f
        var g = 0f
        var b = 0f
        var total = 0f
        val hsv = FloatArray(3)
        for (y in 0 until small.height) for (x in 0 until small.width) {
            val c = small.getPixel(x, y)
            android.graphics.Color.colorToHSV(c, hsv)
            val w = 0.15f + hsv[1] * hsv[2]
            r += android.graphics.Color.red(c) * w
            g += android.graphics.Color.green(c) * w
            b += android.graphics.Color.blue(c) * w
            total += w
        }
        android.graphics.Color.colorToHSV(android.graphics.Color.rgb((r / total).toInt(), (g / total).toInt(), (b / total).toInt()), hsv)
        hsv[1] = hsv[1].coerceAtMost(0.6f)
        hsv[2] = 0.36f
        Color(android.graphics.Color.HSVToColor(hsv))
    }.getOrDefault(Sonora.Card2)
}
