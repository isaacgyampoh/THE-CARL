package app.zazi.ui.brand

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.LinearOutSlowInEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * How long each part of the brand introduction takes.
 *
 * <p>Separated from the composable so the timing can be asserted rather than watched. The
 * numbers are short on purpose: this runs while the session is being restored, so it costs
 * the user nothing when startup is slow, and it must not cost them anything when startup is
 * fast either.</p>
 */
object BrandIntroTiming {
    const val MARK_MILLIS = 260
    const val LETTER_STAGGER_MILLIS = 55
    const val LETTER_MILLIS = 220
    const val TAGLINE_MILLIS = 240
    const val HOLD_MILLIS = 260
    const val EXIT_MILLIS = 220

    /** Letters in the wordmark, used to work out the reveal's total length. */
    const val LETTERS = 4

    /**
     * The whole sequence, end to end.
     *
     * <p>Reduced motion collapses it to a single short fade: the brand is still shown, and
     * somebody who has asked the system for less movement is not made to wait longer than
     * everybody else to get past it.</p>
     */
    fun totalMillis(reducedMotion: Boolean): Int =
        if (reducedMotion) {
            REDUCED_MOTION_MILLIS
        } else {
            MARK_MILLIS +
                (LETTERS - 1) * LETTER_STAGGER_MILLIS + LETTER_MILLIS +
                TAGLINE_MILLIS + HOLD_MILLIS
        }

    const val REDUCED_MOTION_MILLIS = 320
}

/**
 * The Zazi brand introduction.
 *
 * <p>Deep green, the mark resolving, the name arriving letter by letter, one line saying what
 * the product is, and out. Restraint is the point: this is the first thing somebody sees
 * before handing over credentials, and a financial product earns trust by looking composed
 * rather than by performing.</p>
 *
 * <p>It does not gate anything. [onFinished] reports that the animation is over; the caller
 * decides when to move on, which on a cold start is whenever session restore has also
 * finished. No timer sits between the user and their data.</p>
 */
@Composable
fun ZaziBrandIntro(
    reducedMotion: Boolean,
    onFinished: () -> Unit,
    modifier: Modifier = Modifier
) {
    val background = BrandColours.deepGreen
    val mark = BrandColours.lime
    val ink = BrandColours.onDeepGreen

    // One driver for the whole sequence, advanced once. Each element reads the slice of it
    // that belongs to it, so the parts cannot drift out of step with one another.
    val progress = remember { Animatable(0f) }
    val exit = remember { Animatable(0f) }

    LaunchedEffect(reducedMotion) {
        progress.animateTo(
            targetValue = 1f,
            animationSpec = tween(
                durationMillis = BrandIntroTiming.totalMillis(reducedMotion),
                easing = LinearOutSlowInEasing
            )
        )
        exit.animateTo(
            targetValue = 1f,
            animationSpec = tween(
                durationMillis = if (reducedMotion) 0 else BrandIntroTiming.EXIT_MILLIS
            )
        )
        onFinished()
    }

    val total = BrandIntroTiming.totalMillis(reducedMotion).toFloat()
    fun phase(startMillis: Int, durationMillis: Int): Float {
        if (reducedMotion) return progress.value
        val start = startMillis / total
        val end = (startMillis + durationMillis) / total
        return ((progress.value - start) / (end - start)).coerceIn(0f, 1f)
    }

    val markPhase = phase(0, BrandIntroTiming.MARK_MILLIS)
    val taglineStart = BrandIntroTiming.MARK_MILLIS +
        (BrandIntroTiming.LETTERS - 1) * BrandIntroTiming.LETTER_STAGGER_MILLIS +
        BrandIntroTiming.LETTER_MILLIS
    val taglinePhase = phase(taglineStart, BrandIntroTiming.TAGLINE_MILLIS)

    Box(
        modifier = modifier
            .fillMaxSize()
            .background(background)
            // The whole screen announces itself once, as the brand. A screen reader should not
            // walk four separate letters.
            .clearAndSetSemantics { contentDescription = "Zazi" }
            .alpha(1f - exit.value),
        contentAlignment = Alignment.Center
    ) {
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            modifier = Modifier.padding(horizontal = 32.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Canvas(
                    modifier = Modifier
                        .size(40.dp)
                        .alpha(markPhase)
                        // Settles into place rather than zooming at the viewer.
                        .scale(0.88f + 0.12f * markPhase)
                ) {
                    drawZaziMark(mark)
                }

                Spacer(Modifier.width(14.dp))

                // Letter by letter, left to right, each one rising a little as it arrives.
                "Zazi".forEachIndexed { index, character ->
                    val letterPhase = phase(
                        startMillis = BrandIntroTiming.MARK_MILLIS +
                            index * BrandIntroTiming.LETTER_STAGGER_MILLIS,
                        durationMillis = BrandIntroTiming.LETTER_MILLIS
                    )

                    Text(
                        character.toString(),
                        style = TextStyle(
                            fontSize = 46.sp,
                            fontWeight = FontWeight.Bold,
                            letterSpacing = (-1).sp
                        ),
                        color = ink,
                        modifier = Modifier
                            .alpha(letterPhase)
                            .padding(top = (10 * (1f - letterPhase)).dp)
                    )
                }
            }

            Spacer(Modifier.height(14.dp))

            Text(
                // What Zazi is, in words the product can stand behind. It observes and
                // reconciles transactions; it does not move money, and the line must not
                // imply that it does.
                "Every transaction, accounted for.",
                style = TextStyle(fontSize = 15.sp),
                color = BrandColours.onDeepGreenMuted,
                textAlign = TextAlign.Center,
                modifier = Modifier.alpha(taglinePhase)
            )
        }
    }
}

/**
 * The brand's fixed colours.
 *
 * <p>The introduction runs before the app's theme is meaningful — it is the same deep green in
 * light and dark, because a brand does not change colour depending on a system setting — so
 * these are stated here rather than read from the colour scheme.</p>
 */
object BrandColours {
    val deepGreen = Color(0xFF16250A)
    val lime = Color(0xFFC6F432)
    val onDeepGreen = Color(0xFFF2F8E4)
    val onDeepGreenMuted = Color(0xFFA8B79A)
}
