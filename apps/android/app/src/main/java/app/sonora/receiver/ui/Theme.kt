package app.sonora.receiver.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.addPathNodes
import androidx.compose.ui.graphics.vector.path
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.sonora.receiver.R

// Tokens shared with the Windows notch ("Sonora notch" canvas).
object Sonora {
    val Ink = Color(0xFF0A0A0B)
    val Card = Color(0xFF151517)
    val Card2 = Color(0xFF1E1E21)
    val Text = Color(0xFFF5F5F7)
    val Text2 = Color(0xFFA1A1A6)
    val Text3 = Color(0xFF8A8A90)
    val Signal = Color(0xFFA3B8FF)
    val Track = Color(0xFF2C2C31)
    val Live = Color(0xFF5CD69A)
    val Warn = Color(0xFFFFB347)
    val Bad = Color(0xFFFF6B6B)
    val Line = Color(0x14FFFFFF)
}

val MonaSans = FontFamily(
    Font(R.font.mona_sans_regular, FontWeight.Normal),
    Font(R.font.mona_sans_medium, FontWeight.Medium),
    Font(R.font.mona_sans_semibold, FontWeight.SemiBold),
)

fun text(size: Int, weight: FontWeight = FontWeight.Normal, color: Color = Sonora.Text, tracking: Float = 0f) =
    TextStyle(fontFamily = MonaSans, fontSize = size.sp, fontWeight = weight, color = color, letterSpacing = (tracking * size).sp)

@Composable
fun SonoraTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = darkColorScheme(background = Sonora.Ink, surface = Sonora.Card, primary = Sonora.Text, onPrimary = Sonora.Ink),
        content = content,
    )
}

// 24×24 stroke icons, the same outlines the Windows app draws.
object Icons {
    private fun rr(x: Float, y: Float, w: Float, h: Float, r: Float) =
        "M${x + r},$y H${x + w - r} A$r,$r 0 0 1 ${x + w},${y + r} V${y + h - r} A$r,$r 0 0 1 ${x + w - r},${y + h} " +
            "H${x + r} A$r,$r 0 0 1 $x,${y + h - r} V${y + r} A$r,$r 0 0 1 ${x + r},$y Z "

    private fun icon(name: String, data: String, fill: Boolean = false) = ImageVector.Builder(name, 24.dp, 24.dp, 24f, 24f).apply {
        addPath(
            pathData = addPathNodes(data),
            fill = if (fill) SolidColor(Color.White) else null,
            stroke = SolidColor(Color.White),
            strokeLineWidth = 1.8f,
            strokeLineCap = StrokeCap.Round,
            strokeLineJoin = StrokeJoin.Round,
        )
    }.build()

    val Phone = icon("phone", rr(6.5f, 2.5f, 11f, 19f, 2.6f) + "M10.5,18.5 H13.5")
    val Pc = icon("pc", rr(2.5f, 4f, 19f, 12.5f, 2f) + "M8.5,20.5 H15.5 M12,16.5 V20.5")
    val Speaker = icon("speaker", "M4,9.5 H7.2 L12,5.5 V18.5 L7.2,14.5 H4 Z M15.5,9 A4.2,4.2 0 0 1 15.5,15 M18,6.5 A8,8 0 0 1 18,17.5")
    val Pause = icon("pause", "M9,5.5 V18.5 M15,5.5 V18.5")
    val Play = icon("play", "M8,5.5 V18.5 L18.5,12 Z", fill = true)
    val Unlink = icon("unlink", "M9,17 H7 A5,5 0 0 1 7,7 H9 M15,7 H17 A5,5 0 0 1 21,15 M8,12 H11 M3,3 L21,21")
    val Back = icon("back", "M15,6 L9,12 L15,18")
    val Right = icon("right", "M9,6 L15,12 L9,18")
    val Qr = icon("qr", rr(3.5f, 3.5f, 6f, 6f, 1f) + rr(14.5f, 3.5f, 6f, 6f, 1f) + rr(3.5f, 14.5f, 6f, 6f, 1f) +
        "M14.5,14.5 H16.5 V16.5 M20.5,14.5 V20.5 H14.5 M17.5,20.5 V17.5")
    val Headphones = icon("headphones", "M4.5,15.5 V12.5 A7.5,7.5 0 0 1 19.5,12.5 V15.5 " + rr(3.5f, 14f, 4.5f, 6.5f, 1.8f) + rr(16f, 14f, 4.5f, 6.5f, 1.8f))
    val Bluetooth = icon("bluetooth", "M7,7.5 L17,16.5 L12,21 V3 L17,7.5 L7,16.5")
    val Auto = icon("auto", "M12,3.5 L13.7,10.3 L20.5,12 L13.7,13.7 L12,20.5 L10.3,13.7 L3.5,12 L10.3,10.3 Z")
    val Check = icon("check", "M5,12.5 L10,17.5 L19,7")
    val Timer = icon("timer", "M4.5,13 A7.5,7.5 0 1 1 19.5,13 A7.5,7.5 0 1 1 4.5,13 Z M12,13 V9 M9.5,2.5 H14.5 M12,2.5 V5.5")
    val Previous = icon("previous", "M18,6.5 L9.5,12 L18,17.5 Z M6.5,6.5 V17.5", fill = true)
    val Next = icon("next", "M6,6.5 L14.5,12 L6,17.5 Z M17.5,6.5 V17.5", fill = true)
    val Music = icon("music", "M9,17.5 V6 L19,4 V15.5 M9,17.5 A2.5,2.5 0 1 1 4,17.5 A2.5,2.5 0 1 1 9,17.5 Z M19,15.5 A2.5,2.5 0 1 1 14,15.5 A2.5,2.5 0 1 1 19,15.5 Z")
    val Down = icon("down", "M6,9 L12,15 L18,9")
    val Open = icon("open", "M7,17 L17,7 M9,7 H17 V15")
    val Muted = icon("muted", "M4,9.5 H7.2 L12,5.5 V18.5 L7.2,14.5 H4 Z M16,9.5 L21,14.5 M21,9.5 L16,14.5")
    val Logo = ImageVector.Builder("logo", 24.dp, 24.dp, 24f, 24f).apply {
        path(stroke = SolidColor(Color.White), strokeAlpha = 0.5f, strokeLineWidth = 2.2f, strokeLineCap = StrokeCap.Round) {
            moveTo(4f, 9.5f); curveTo(7f, 5.5f, 9f, 5.5f, 12f, 9.5f); reflectiveCurveTo(17f, 13.5f, 20f, 9.5f)
        }
        path(stroke = SolidColor(Color.White), strokeLineWidth = 2.2f, strokeLineCap = StrokeCap.Round) {
            moveTo(4f, 16f); curveTo(7f, 10f, 9f, 10f, 12f, 16f); reflectiveCurveTo(17f, 22f, 20f, 16f)
        }
    }.build()
}
