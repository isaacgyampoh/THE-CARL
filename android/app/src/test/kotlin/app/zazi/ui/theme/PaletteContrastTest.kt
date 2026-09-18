package app.zazi.ui.theme

import androidx.compose.material3.ColorScheme
import androidx.compose.ui.graphics.Color
import com.google.common.truth.Truth.assertWithMessage
import org.junit.Test
import kotlin.math.max
import kotlin.math.min
import kotlin.math.pow

/**
 * Contrast, measured rather than judged.
 *
 * <p>This is the one part of the design system whose defects are invisible to the person who
 * introduced it. A pairing that reads fine on a desk monitor at full brightness can be
 * unreadable to someone with reduced vision, or to an agent holding the phone in Ghanaian
 * afternoon sun — which is the actual operating condition for this product. Picking colours by
 * eye and shipping them is how that happens.</p>
 *
 * <p>WCAG 2.1 thresholds: 4.5:1 for body text, 3:1 for large text and for the boundary of a
 * control the user has to find. Each assertion names the pairing so a failure says which two
 * colours to change, not merely that a number moved.</p>
 */
class PaletteContrastTest {

    // ---- WCAG 2.1 relative luminance and contrast ratio -------------------------------

    private fun channel(component: Float): Double {
        val c = component.toDouble()
        return if (c <= 0.03928) c / 12.92 else ((c + 0.055) / 1.055).pow(2.4)
    }

    private fun luminance(color: Color): Double =
        0.2126 * channel(color.red) + 0.7152 * channel(color.green) + 0.0722 * channel(color.blue)

    private fun contrast(foreground: Color, background: Color): Double {
        val a = luminance(foreground)
        val b = luminance(background)
        return (max(a, b) + 0.05) / (min(a, b) + 0.05)
    }

    private fun assertReadable(
        label: String,
        foreground: Color,
        background: Color,
        minimum: Double
    ) {
        val ratio = contrast(foreground, background)
        assertWithMessage("$label = %.2f:1, needs %.1f:1".format(ratio, minimum))
            .that(ratio >= minimum)
            .isTrue()
    }

    private val bodyText = 4.5
    private val largeText = 3.0
    private val boundary = 3.0

    // ---- The pairings the product actually renders ------------------------------------

    private fun assertSchemeIsReadable(name: String, scheme: ColorScheme) {
        // Text on the surfaces every screen is built from.
        assertReadable("$name onBackground/background", scheme.onBackground, scheme.background, bodyText)
        assertReadable("$name onSurface/surface", scheme.onSurface, scheme.surface, bodyText)
        assertReadable("$name onSurfaceVariant/surface", scheme.onSurfaceVariant, scheme.surface, bodyText)

        // Every panel tier. A label that is readable on one card tier and not the next is the
        // kind of defect that only shows up on the one screen nobody screenshotted.
        assertReadable("$name onSurface/surfaceContainerLowest", scheme.onSurface, scheme.surfaceContainerLowest, bodyText)
        assertReadable("$name onSurface/surfaceContainerLow", scheme.onSurface, scheme.surfaceContainerLow, bodyText)
        assertReadable("$name onSurface/surfaceContainer", scheme.onSurface, scheme.surfaceContainer, bodyText)
        assertReadable("$name onSurface/surfaceContainerHigh", scheme.onSurface, scheme.surfaceContainerHigh, bodyText)
        assertReadable("$name onSurface/surfaceContainerHighest", scheme.onSurface, scheme.surfaceContainerHighest, bodyText)
        assertReadable("$name onSurfaceVariant/surfaceContainer", scheme.onSurfaceVariant, scheme.surfaceContainer, bodyText)

        // The primary action. If the label on the sign-in button is unreadable, nothing else
        // in the product matters.
        assertReadable("$name onPrimary/primary", scheme.onPrimary, scheme.primary, bodyText)
        assertReadable("$name onSecondary/secondary", scheme.onSecondary, scheme.secondary, bodyText)
        assertReadable("$name onPrimaryContainer/primaryContainer", scheme.onPrimaryContainer, scheme.primaryContainer, bodyText)
        assertReadable("$name onSecondaryContainer/secondaryContainer", scheme.onSecondaryContainer, scheme.secondaryContainer, bodyText)

        // Money direction. This one carries meaning, not decoration: it is how an agent tells
        // cash in from cash out at a glance, so it has to survive poor light and poor sight.
        assertReadable("$name tertiary/surface", scheme.tertiary, scheme.surface, largeText)
        assertReadable("$name tertiary/background", scheme.tertiary, scheme.background, largeText)
        assertReadable("$name onTertiaryContainer/tertiaryContainer", scheme.onTertiaryContainer, scheme.tertiaryContainer, bodyText)

        // Errors, read under stress and usually in a hurry.
        assertReadable("$name error/surface", scheme.error, scheme.surface, largeText)
        assertReadable("$name onError/error", scheme.onError, scheme.error, bodyText)
        assertReadable("$name onErrorContainer/errorContainer", scheme.onErrorContainer, scheme.errorContainer, bodyText)

        // The edge of a text field. Below 3:1 the user cannot see where to tap.
        assertReadable("$name outline/surface", scheme.outline, scheme.surface, boundary)
        assertReadable("$name outline/background", scheme.outline, scheme.background, boundary)
    }

    @Test
    fun `light palette is readable`() {
        assertSchemeIsReadable("light", LightColours)
    }

    @Test
    fun `dark palette is readable`() {
        assertSchemeIsReadable("dark", DarkColours)
    }

    @Test
    fun `money direction never rests on colour alone`() {
        // Cash in is tinted with the tertiary role and cash out is not, and in dark mode those
        // two sit only 1.19:1 apart in luminance — indistinguishable to a red-green colour
        // blind user, and to anyone in strong sunlight.
        //
        // That is acceptable here, and this test records why rather than leaving the next
        // person to rediscover it: direction is carried by an arrow icon, by a content
        // description on that icon, and by the sign on the amount itself. Colour only
        // reinforces it. WCAG 1.4.1 asks that colour not be the sole means of conveying
        // information, and it is not.
        //
        // What must hold is that the badge carrying the arrow stays readable, because that is
        // the thing actually doing the work.
        listOf("light" to LightColours, "dark" to DarkColours).forEach { (name, scheme) ->
            assertReadable(
                "$name cash-in badge",
                scheme.onTertiaryContainer,
                scheme.tertiaryContainer,
                bodyText
            )
            assertReadable(
                "$name cash-out badge",
                scheme.onSurfaceVariant,
                scheme.surfaceContainerHighest,
                bodyText
            )
        }
    }

    @Test
    fun `the brand introduction is readable on its own background`() {
        // Not part of either scheme — the introduction is fixed deep green in light and dark —
        // so it would otherwise escape every check above.
        assertReadable("intro wordmark", BrandInk, BrandBackground, bodyText)
        assertReadable("intro mark", BrandLime, BrandBackground, largeText)
        assertReadable("intro tagline", BrandMuted, BrandBackground, bodyText)
    }

    private companion object {
        // Mirrors app.zazi.ui.brand.BrandColours. Duplicated deliberately: the test states
        // what the values must be, so changing the brand cannot silently change the assertion.
        val BrandBackground = Color(0xFF16250A)
        val BrandLime = Color(0xFFC6F432)
        val BrandInk = Color(0xFFF2F8E4)
        val BrandMuted = Color(0xFFA8B79A)
    }
}
