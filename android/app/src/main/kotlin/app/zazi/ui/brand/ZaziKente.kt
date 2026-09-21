package app.zazi.ui.brand

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.clipRect
import androidx.compose.ui.graphics.drawscope.translate
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/**
 * The kente-inspired woven pattern, drawn natively.
 *
 * <p>The same tile as the web portal's <c>img/kente.svg</c> — the same strips, the same
 * colours, the second row offset from the first the way kente strips are sewn side by side —
 * so an agent sees one product on the handset and in the owner's portal. Drawn as geometry
 * rather than shipped as an image: crisp at any density, nothing to decode, no asset to keep
 * in step.</p>
 */
private object Kente {
    val Gold = Color(0xFFE3B23C)
    val Green = Color(0xFF2F5D12)
    val Lime = Color(0xFFC6F432)
    val Black = Color(0xFF16200F)
    val Red = Color(0xFFB8321F)
    val Leaf = Color(0xFF3E7A1A)

    /** The deep-green wash laid over the pattern wherever words sit on it. */
    val Wash = Brush.verticalGradient(
        0f to Color(0xCC0E1E06),
        0.55f to Color(0xEB0E1E06),
        1f to Color(0xF70A1604)
    )
}

/** One 160 × 160 tile, in tile units, at [scale]. */
private fun DrawScope.kenteTile(scale: Float) {
    fun rect(x: Float, y: Float, w: Float, h: Float, color: Color) =
        drawRect(color, Offset(x * scale, y * scale), Size(w * scale, h * scale))

    fun diamond(cx: Float, cy: Float, r: Float, color: Color) {
        val path = Path().apply {
            moveTo(cx * scale, (cy - r) * scale)
            lineTo((cx + r) * scale, cy * scale)
            lineTo(cx * scale, (cy + r) * scale)
            lineTo((cx - r) * scale, cy * scale)
            close()
        }
        drawPath(path, color)
    }

    fun gold(x: Float, y: Float) {
        rect(x, y, 40f, 80f, Kente.Gold)
        rect(x, y + 10, 40f, 5f, Kente.Black)
        rect(x, y + 20, 40f, 5f, Kente.Black)
        rect(x, y + 42, 40f, 16f, Kente.Red)
        rect(x + 15, y + 45, 10f, 10f, Kente.Gold)
        rect(x, y + 68, 40f, 3f, Kente.Black)
    }

    fun green(x: Float, y: Float) {
        rect(x, y, 40f, 80f, Kente.Green)
        diamond(x + 20, y + 20, 12f, Kente.Lime)
        diamond(x + 20, y + 20, 6f, Kente.Green)
        diamond(x + 20, y + 60, 12f, Kente.Gold)
        rect(x + 2, y + 38, 36f, 3f, Kente.Lime)
    }

    fun black(x: Float, y: Float) {
        rect(x, y, 40f, 80f, Kente.Black)
        rect(x + 6, y, 4f, 80f, Kente.Gold)
        rect(x + 30, y, 4f, 80f, Kente.Gold)
        rect(x + 10, y + 28, 20f, 24f, Kente.Red)
        rect(x + 15, y + 33, 10f, 14f, Kente.Black)
    }

    fun check(x: Float, y: Float) {
        rect(x, y, 40f, 80f, Kente.Leaf)
        for (col in 0 until 4) {
            val first = if (col % 2 == 0) Kente.Gold else Kente.Black
            val second = if (col % 2 == 0) Kente.Black else Kente.Gold
            rect(x + col * 10, y + 30, 10f, 10f, first)
            rect(x + col * 10, y + 40, 10f, 10f, second)
        }
        rect(x, y + 8, 40f, 4f, Kente.Lime)
        rect(x, y + 68, 40f, 4f, Kente.Lime)
    }

    gold(0f, 0f); green(40f, 0f); black(80f, 0f); check(120f, 0f)
    black(0f, 80f); check(40f, 80f); gold(80f, 80f); green(120f, 80f)
}

/** Repeats the tile across the whole canvas; [tile] is the drawn width of one tile. */
private fun DrawScope.kente(tile: Float) {
    val scale = tile / 160f
    clipRect {
        var y = 0f
        while (y < size.height) {
            var x = 0f
            while (x < size.width) {
                translate(x, y) { kenteTile(scale) }
                x += tile
            }
            y += tile
        }
    }
}

/**
 * The woven pattern under a deep-green wash, with room for content on top — the header band
 * on the sign-in and activation screens.
 */
@Composable
fun KenteBackground(
    modifier: Modifier = Modifier,
    tileSize: Dp = 96.dp,
    content: @Composable BoxScope.() -> Unit
) {
    Box(modifier.clipToBounds()) {
        Canvas(Modifier.matchParentSize()) {
            kente(tileSize.toPx())
            drawRect(Kente.Wash)
        }
        content()
    }
}

/** The pattern at full colour, as a thin woven strip along an edge. */
@Composable
fun KenteStrip(modifier: Modifier = Modifier, height: Dp = 6.dp) {
    Canvas(modifier.fillMaxWidth().height(height)) { kente(height.toPx() * 4) }
}

/** Colours for words set on the band, so they stay readable on the wash. */
object KenteOn {
    val Text = Color(0xFFF4F8EC)
    val Muted = Color(0xFFD5E2C4)
    val Accent = Kente.Lime
}
