package app.zazi.ui.brand

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * The Zazi mark.
 *
 * <p>Two bars and the stroke that joins them. The bars are deliberately offset — the upper
 * one sits left, the lower one right — because the product's whole job is reconciling two
 * sides that do not line up, and the diagonal is what brings them together. It reads as a Z
 * without being a letter dropped into a circle.</p>
 *
 * <p>Drawn as geometry rather than shipped as an image: it stays crisp at any size, costs
 * nothing to decode, and recolours with the theme. Solid fills only — no gradient anywhere
 * in the identity.</p>
 */
@Composable
fun ZaziMark(
    modifier: Modifier = Modifier,
    color: Color = MaterialTheme.colorScheme.onPrimaryContainer
) {
    Canvas(modifier = modifier.size(32.dp)) { drawZaziMark(color) }
}

/**
 * The geometry, shared by every rendering of the mark.
 *
 * <p>Expressed as fractions of the canvas so the same path serves a 24dp mark and a launcher
 * icon without a second set of numbers to keep in step.</p>
 */
fun DrawScope.drawZaziMark(color: Color) {
    val w = size.width
    val h = size.height

    // Bar thickness and the horizontal offset between the two bars. The offset is the whole
    // idea of the mark; at less than about a tenth it stops reading as deliberate.
    val bar = h * 0.20f
    val shift = w * 0.12f

    val path = Path().apply {
        // Upper bar, sitting left.
        moveTo(0f, 0f)
        lineTo(w - shift, 0f)
        lineTo(w - shift, bar)
        // Down the diagonal to the start of the lower bar.
        lineTo(shift + bar * 0.9f, h - bar)
        lineTo(w, h - bar)
        lineTo(w, h)
        // Lower bar, sitting right.
        lineTo(shift, h)
        lineTo(shift, h - bar)
        lineTo(w - shift - bar * 0.9f, bar)
        lineTo(0f, bar)
        close()
    }

    drawPath(path, color)
}

/**
 * The wordmark: the symbol beside the name.
 *
 * <p>The name is set in the app's own type rather than an imported display face, tightened
 * and weighted so it reads as a mark rather than as a heading that happens to say Zazi.</p>
 */
@Composable
fun ZaziWordmark(
    modifier: Modifier = Modifier,
    markColor: Color = MaterialTheme.colorScheme.primary,
    textColor: Color = MaterialTheme.colorScheme.onSurface,
    markSize: androidx.compose.ui.unit.Dp = 28.dp,
    textSize: androidx.compose.ui.unit.TextUnit = 34.sp
) {
    Row(
        // One label for the pair. A screen reader announcing "Zazi" twice — once for the
        // symbol and once for the name — is noise.
        modifier = modifier.clearAndSetSemantics { contentDescription = "Zazi" },
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.Start
    ) {
        Canvas(Modifier.size(markSize)) { drawZaziMark(markColor) }
        Spacer(Modifier.width(10.dp))
        Text(
            "Zazi",
            style = MaterialTheme.typography.displaySmall.copy(
                fontSize = textSize,
                fontWeight = FontWeight.Bold,
                // Drawn in slightly, so the name sits as one shape rather than four letters.
                letterSpacing = (-1).sp
            ),
            color = textColor
        )
    }
}
