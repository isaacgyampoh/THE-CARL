package app.zazi.ui.theme

import android.app.Activity
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat

/**
 * The application's colours.
 *
 * <p>Deliberately not Material You dynamic colour. Zazi shows money and reconciliation
 * state, and the colours that carry meaning here — a held item, a variance, a failed sync —
 * have to mean the same thing on every handset. Dynamic colour rederives the palette from
 * the user's wallpaper, which would let one agent's "needs attention" be another's ordinary
 * background and would drift away from the web dashboard an owner reads alongside it.</p>
 *
 * <p>The values are the dashboard's, so the two surfaces of the same product look like one
 * product.</p>
 */
private val Lime = Color(0xFFC6F432)
private val DeepGreen = Color(0xFF1E3A12)

/**
 * Money in, as a colour.
 *
 * <p>A separate green from the brand one, and dark enough to read as text on white. The
 * brand lime is a background colour: as small text on a light surface it fails contrast
 * badly, which is exactly where an amount lives.</p>
 *
 * <p>It is never the only signal. Every amount also carries its sign, so the direction
 * survives a monochrome screen, a colour-blind reader and a bright market stall.</p>
 */
private val MoneyIn = Color(0xFF3F6B14)
private val MoneyInDark = Color(0xFFB8E86A)

private val LightColours = lightColorScheme(
    // Deep green carries the primary actions; the lime is the highlight it sits against.
    primary = DeepGreen,
    onPrimary = Color(0xFFFFFFFF),
    // The balance panel. Lime with near-black green on it — the one thing an agent looks
    // for first, and the highest-contrast pairing in the palette.
    primaryContainer = Lime,
    onPrimaryContainer = Color(0xFF16250A),
    // Selected chips use the secondary container. Left undefined it falls back to a grey
    // barely distinguishable from an unselected chip — which on the capture form meant the
    // cash-in/cash-out choice, the costliest mistake available there, was hard to read at a
    // glance. Tinted towards the brand so "chosen" is unmistakable.
    secondary = DeepGreen,
    onSecondary = Color(0xFFFFFFFF),
    secondaryContainer = Color(0xFFDCF39A),
    onSecondaryContainer = Color(0xFF16250A),
    // Money in. Material has no "positive" slot, so it lives in tertiary rather than as a
    // loose constant nothing else knows about.
    tertiary = MoneyIn,
    onTertiary = Color(0xFFFFFFFF),
    tertiaryContainer = Color(0xFFE4F7BE),
    onTertiaryContainer = Color(0xFF1B3B00),
    background = Color(0xFFFAFBF5),
    onBackground = Color(0xFF181D12),
    surface = Color(0xFFFFFFFF),
    onSurface = Color(0xFF181D12),
    surfaceVariant = Color(0xFFEFF3E4),
    // The surfaceContainer family, defined explicitly. Card and Surface take their default
    // container colour from these roles, not from surface or surfaceVariant — leaving them
    // undefined let Material's baseline purple through, so every card on a green dashboard
    // rendered lavender. The same trap as the chips: an unnamed role is not a neutral
    // default, it is somebody else's brand.
    surfaceDim = Color(0xFFDDE3D2),
    surfaceBright = Color(0xFFFAFBF5),
    surfaceContainerLowest = Color(0xFFFFFFFF),
    surfaceContainerLow = Color(0xFFF5F8EC),
    surfaceContainer = Color(0xFFEFF3E4),
    surfaceContainerHigh = Color(0xFFE9EEDD),
    surfaceContainerHighest = Color(0xFFE3E9D6),
    surfaceTint = DeepGreen,
    inverseSurface = Color(0xFF2D3327),
    inverseOnSurface = Color(0xFFEFF3E4),
    inversePrimary = Lime,
    // The dashboard's "muted": labels and secondary lines, still readable rather than faint.
    onSurfaceVariant = Color(0xFF5F6B52),
    outline = Color(0xFFC3CDB4),
    outlineVariant = Color(0xFFE3E9D6),
    error = Color(0xFFB3261E),
    onError = Color(0xFFFFFFFF),
    errorContainer = Color(0xFFFDECEB),
    onErrorContainer = Color(0xFF5C1512)
)

private val DarkColours = darkColorScheme(
    // The lime becomes the primary on dark, because deep green on a dark surface is
    // unreadable — and text drawn on it darkens to match.
    primary = Lime,
    onPrimary = Color(0xFF16250A),
    primaryContainer = Color(0xFF2C4A18),
    onPrimaryContainer = Color(0xFFDCF7A6),
    secondary = Lime,
    onSecondary = Color(0xFF16250A),
    secondaryContainer = Color(0xFF3A5A20),
    onSecondaryContainer = Color(0xFFE7FBC0),
    tertiary = MoneyInDark,
    onTertiary = Color(0xFF1B3B00),
    tertiaryContainer = Color(0xFF2E4D12),
    onTertiaryContainer = Color(0xFFD6F5A8),
    background = Color(0xFF0E140A),
    onBackground = Color(0xFFE6EEDC),
    // Lifted rather than pure black: a reconciliation screen is read for minutes at a time,
    // and maximum contrast is tiring over that long.
    surface = Color(0xFF171E12),
    onSurface = Color(0xFFE6EEDC),
    surfaceVariant = Color(0xFF253019),
    surfaceDim = Color(0xFF0E140A),
    surfaceBright = Color(0xFF343B2D),
    surfaceContainerLowest = Color(0xFF090D06),
    surfaceContainerLow = Color(0xFF171E12),
    surfaceContainer = Color(0xFF1B2216),
    surfaceContainerHigh = Color(0xFF252D1F),
    surfaceContainerHighest = Color(0xFF303829),
    surfaceTint = Lime,
    inverseSurface = Color(0xFFE6EEDC),
    inverseOnSurface = Color(0xFF2D3327),
    inversePrimary = DeepGreen,
    onSurfaceVariant = Color(0xFFA8B79A),
    outline = Color(0xFF4A5A3C),
    outlineVariant = Color(0xFF2E3A24),
    error = Color(0xFFFF8A80),
    onError = Color(0xFF3A1D1C),
    errorContainer = Color(0xFF3A1D1C),
    onErrorContainer = Color(0xFFFFDAD6)
)

/**
 * Wraps the application in the Zazi palette, following the system light/dark setting.
 *
 * @param darkTheme overridable so a screenshot or a test can pin one scheme.
 */
@Composable
fun ZaziTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit
) {
    val view = LocalView.current

    if (!view.isInEditMode) {
        // The app draws edge to edge, so the status bar sits on the app's own background
        // rather than a strip the system colours. The icons do not follow automatically:
        // on a light background they stayed light and the clock, signal and battery were
        // barely legible. They have to be told which way round the background is.
        SideEffect {
            val window = (view.context as Activity).window
            WindowCompat.getInsetsController(window, view).apply {
                isAppearanceLightStatusBars = !darkTheme
                isAppearanceLightNavigationBars = !darkTheme
            }
        }
    }

    MaterialTheme(
        colorScheme = if (darkTheme) DarkColours else LightColours,
        content = content
    )
}
