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
private val BrandAccent = Color(0xFF5B3FA8)
private val BrandAccentDark = Color(0xFFB9A3EF)

private val LightColours = lightColorScheme(
    primary = BrandAccent,
    onPrimary = Color(0xFFFFFFFF),
    primaryContainer = Color(0xFFE9E1FA),
    onPrimaryContainer = Color(0xFF1F1136),
    // Selected chips use the secondary container. Left undefined it falls back to a grey
    // barely distinguishable from an unselected chip — which on the capture form meant the
    // cash-in/cash-out choice, the costliest mistake available there, was hard to read at a
    // glance. Tinted towards the brand so "chosen" is unmistakable.
    secondary = Color(0xFF5B3FA8),
    onSecondary = Color(0xFFFFFFFF),
    secondaryContainer = Color(0xFFD7C9F5),
    onSecondaryContainer = Color(0xFF1F1136),
    background = Color(0xFFFAF8FD),
    onBackground = Color(0xFF1A1523),
    surface = Color(0xFFFFFFFF),
    onSurface = Color(0xFF1A1523),
    surfaceVariant = Color(0xFFF2EFFA),
    // The dashboard's "muted": labels and secondary lines, still readable rather than faint.
    onSurfaceVariant = Color(0xFF6B6478),
    outline = Color(0xFFCFC8DA),
    outlineVariant = Color(0xFFE6E1EC),
    error = Color(0xFFB3261E),
    onError = Color(0xFFFFFFFF),
    errorContainer = Color(0xFFFDECEB),
    onErrorContainer = Color(0xFF5C1512)
)

private val DarkColours = darkColorScheme(
    // The accent lightens so it stays legible on a dark surface, which means text drawn on
    // it has to darken to match — white on this would fail contrast.
    primary = BrandAccentDark,
    onPrimary = Color(0xFF221C2E),
    primaryContainer = Color(0xFF3B2E63),
    onPrimaryContainer = Color(0xFFE9E1FA),
    secondary = Color(0xFFB9A3EF),
    onSecondary = Color(0xFF221C2E),
    secondaryContainer = Color(0xFF4B3A7A),
    onSecondaryContainer = Color(0xFFEDE4FF),
    background = Color(0xFF15121C),
    onBackground = Color(0xFFECE7F2),
    // Lifted rather than pure black: a reconciliation screen is read for minutes at a time,
    // and maximum contrast is tiring over that long.
    surface = Color(0xFF1E1A27),
    onSurface = Color(0xFFECE7F2),
    surfaceVariant = Color(0xFF2A2338),
    onSurfaceVariant = Color(0xFFA49BB4),
    outline = Color(0xFF4A4158),
    outlineVariant = Color(0xFF322B3D),
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
