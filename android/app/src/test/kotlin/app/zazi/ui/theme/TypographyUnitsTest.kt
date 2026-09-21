package app.zazi.ui.theme

import androidx.compose.ui.unit.TextUnitType
import com.google.common.truth.Truth.assertWithMessage
import org.junit.Test

/**
 * Every text style uses sp for letter spacing. Material animates between styles (a text
 * field's label) and throws when one side is em and the other sp — which crashed the capture
 * screen on open.
 */
class TypographyUnitsTest {
    @Test
    fun `letter spacing is never em`() {
        with(ZaziTypography) {
            listOf(
                displayLarge, displayMedium, displaySmall, headlineLarge, headlineMedium, headlineSmall,
                titleLarge, titleMedium, titleSmall, bodyLarge, bodyMedium, bodySmall,
                labelLarge, labelMedium, labelSmall
            ).forEachIndexed { index, style ->
                assertWithMessage("style #$index letterSpacing").that(style.letterSpacing.type)
                    .isNotEqualTo(TextUnitType.Em)
            }
        }
    }
}
