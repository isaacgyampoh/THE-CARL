package app.zazi.ui.theme

import android.app.Activity
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.material3.Typography
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import app.zazi.R
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat

/**
 * The application's colours.
 *
 * <p>Deliberately not Material You dynamic colour. Zazi shows money and reconciliation
 * state, and the colours that carry meaning here — a held item, a variance, a failed sync —
 * have to mean the same thing on every handset, and match the owner's portal.</p>
 *
 * <p>A money product's palette: deep navy ink for the brand and every primary action, Ghana
 * gold as the single accent, and green and red kept for money in and money out. The values are
 * the portal's, so the two surfaces of one product look like one product.</p>
 */
internal object Brand {
    val Navy = Color(0xFF0B1F33)
    val NavyRaised = Color(0xFF13304D)
    val NavyLine = Color(0xFF20405F)
    val Ink = Color(0xFF0F2A44)
    val Gold = Color(0xFFF2A900)
    val OnNavy = Color(0xFFF4F7FB)
    val OnNavyMuted = Color(0xFFA9B8C9)

    /** The operators' own colours, so a row is recognisable before it is read. */
    val Mtn = Color(0xFFFFCB05)
    val OnMtn = Color(0xFF1A1A1A)
    val Telecel = Color(0xFFE30613)
    val AirtelTigo = Color(0xFF0A4DA2)
}

/**
 * Money in, as a colour. Dark enough to read as text on white; never the only signal — every
 * amount also carries its sign.
 */
private val MoneyIn = Color(0xFF067647)
private val MoneyInDark = Color(0xFF5FD39A)

/**
 * Visible to tests so contrast can be asserted rather than assumed. The palette is the one
 * part of the design system where a wrong value is invisible to whoever chose it and decisive
 * for whoever cannot read it.
 */
internal val LightColours = lightColorScheme(
    primary = Brand.Ink,
    onPrimary = Color(0xFFFFFFFF),
    // The balance header: navy with white on it, the highest-contrast pairing in the palette.
    primaryContainer = Brand.Navy,
    onPrimaryContainer = Color(0xFFFFFFFF),
    // Selected chips. Tinted so "chosen" is unmistakable on the capture form, where the
    // cash-in/cash-out choice is the costliest mistake available.
    secondary = Brand.Ink,
    onSecondary = Color(0xFFFFFFFF),
    secondaryContainer = Color(0xFFDDE7F3),
    onSecondaryContainer = Brand.Navy,
    // Money in. Material has no "positive" slot, so it lives in tertiary.
    tertiary = MoneyIn,
    onTertiary = Color(0xFFFFFFFF),
    tertiaryContainer = Color(0xFFE7F6EE),
    onTertiaryContainer = Color(0xFF054A2D),
    // Cool grey page, white cards on it: the look of every banking app an agent has used.
    background = Color(0xFFF4F6F9),
    onBackground = Color(0xFF0E1726),
    surface = Color(0xFFFFFFFF),
    onSurface = Color(0xFF0E1726),
    surfaceVariant = Color(0xFFEEF2F6),
    // Defined explicitly: left undefined, Material's baseline purple shows through cards.
    surfaceDim = Color(0xFFDDE3EA),
    surfaceBright = Color(0xFFFFFFFF),
    surfaceContainerLowest = Color(0xFFFFFFFF),
    surfaceContainerLow = Color(0xFFF7F9FB),
    surfaceContainer = Color(0xFFFFFFFF),
    surfaceContainerHigh = Color(0xFFEEF2F6),
    surfaceContainerHighest = Color(0xFFE6EBF1),
    surfaceTint = Color(0xFFFFFFFF),
    inverseSurface = Color(0xFF1B2735),
    inverseOnSurface = Color(0xFFEEF2F6),
    inversePrimary = Brand.Gold,
    onSurfaceVariant = Color(0xFF5B6676),
    // Above 3:1 against surface, so a field's edge can be found. Enforced by PaletteContrastTest.
    outline = Color(0xFF7D8B9C),
    outlineVariant = Color(0xFFE3E8EF),
    error = Color(0xFFC0362C),
    onError = Color(0xFFFFFFFF),
    errorContainer = Color(0xFFFDECEA),
    onErrorContainer = Color(0xFF5C1512)
)

/** Dark counterpart; see [LightColours] for why this is not private. */
internal val DarkColours = darkColorScheme(
    // Gold takes over as the action colour: navy vanishes on a dark ground.
    primary = Color(0xFFF2B544),
    onPrimary = Color(0xFF1A1200),
    primaryContainer = Brand.NavyRaised,
    onPrimaryContainer = Color(0xFFFFFFFF),
    secondary = Color(0xFFF2B544),
    onSecondary = Color(0xFF1A1200),
    secondaryContainer = Color(0xFF1D3550),
    onSecondaryContainer = Color(0xFFE6EBF2),
    tertiary = MoneyInDark,
    onTertiary = Color(0xFF00341F),
    tertiaryContainer = Color(0xFF0F2E22),
    onTertiaryContainer = Color(0xFFBDF0D5),
    background = Color(0xFF0A121C),
    onBackground = Color(0xFFE6EBF2),
    // Lifted rather than pure black: a reconciliation screen is read for minutes at a time.
    surface = Color(0xFF111C29),
    onSurface = Color(0xFFE6EBF2),
    surfaceVariant = Color(0xFF1A2636),
    surfaceDim = Color(0xFF0A121C),
    surfaceBright = Color(0xFF26364A),
    surfaceContainerLowest = Color(0xFF070D14),
    surfaceContainerLow = Color(0xFF0E1823),
    surfaceContainer = Color(0xFF142131),
    surfaceContainerHigh = Color(0xFF1A2838),
    surfaceContainerHighest = Color(0xFF223244),
    surfaceTint = Color(0xFF142131),
    inverseSurface = Color(0xFFE6EBF2),
    inverseOnSurface = Color(0xFF1B2735),
    inversePrimary = Brand.Ink,
    onSurfaceVariant = Color(0xFF9AA7B8),
    // Dark-mode borders are the first thing to vanish in bright light, which is the
    // condition this product is used in.
    outline = Color(0xFF74859A),
    outlineVariant = Color(0xFF243244),
    error = Color(0xFFFF8A80),
    onError = Color(0xFF3A1D1C),
    errorContainer = Color(0xFF3A1D1C),
    onErrorContainer = Color(0xFFFFDAD6)
)

/**
 * Inter, bundled rather than downloaded: most of this product's phones are offline more than
 * they are online, and a font that arrives late re-flows every figure on the screen. Its
 * figures are tabular, so amounts in a list line up.
 */
private val Inter = FontFamily(
    Font(R.font.inter_regular, FontWeight.Normal),
    Font(R.font.inter_medium, FontWeight.Medium),
    Font(R.font.inter_semibold, FontWeight.SemiBold),
    Font(R.font.inter_bold, FontWeight.Bold)
)

/**
 * Tracking is given as a fraction of the size but stored in sp. Material animates between
 * text styles — a field's label as it floats up — and cannot blend em with sp: mixing the two
 * units crashes the moment a text field is focused.
 */
private fun TextStyle.inter(tracking: Double = 0.0) = copy(
    fontFamily = Inter,
    fontFeatureSettings = "tnum",
    letterSpacing = if (tracking == 0.0) letterSpacing else (tracking * fontSize.value).sp
)

internal val ZaziTypography: Typography = Typography().run {
    copy(
        displayLarge = displayLarge.inter(-0.03),
        displayMedium = displayMedium.inter(-0.03),
        displaySmall = displaySmall.inter(-0.03),
        headlineLarge = headlineLarge.inter(-0.025).copy(fontWeight = FontWeight.Bold),
        headlineMedium = headlineMedium.inter(-0.025).copy(fontWeight = FontWeight.Bold),
        headlineSmall = headlineSmall.inter(-0.02).copy(fontWeight = FontWeight.SemiBold),
        titleLarge = titleLarge.inter(-0.015).copy(fontWeight = FontWeight.SemiBold),
        titleMedium = titleMedium.inter(-0.01),
        titleSmall = titleSmall.inter(),
        bodyLarge = bodyLarge.inter(-0.005),
        bodyMedium = bodyMedium.inter(),
        bodySmall = bodySmall.inter(),
        labelLarge = labelLarge.inter().copy(fontWeight = FontWeight.SemiBold),
        labelMedium = labelMedium.inter(),
        labelSmall = labelSmall.inter()
    )
}

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
        typography = ZaziTypography,
        content = content
    )
}

/**
 * Who is colouring the strip behind the status bar. The app is laid out inside the system
 * bars, so a screen with a navy header would otherwise have a pale band above it with white
 * icons lost against it. A screen that wants the strip calls [StatusBarGround].
 */
val LocalStatusBarGround = staticCompositionLocalOf<(Color?) -> Unit> { {} }

/**
 * Paints the status-bar strip [color] and draws the icons light, for as long as the calling
 * screen is showing; hands both back to the theme when it leaves.
 */
@Composable
fun StatusBarGround(color: Color) {
    val setGround = LocalStatusBarGround.current
    val view = LocalView.current
    val darkTheme = isSystemInDarkTheme()
    DisposableEffect(color, darkTheme) {
        setGround(color)
        val controller = (view.context as? Activity)?.let { WindowCompat.getInsetsController(it.window, view) }
        controller?.isAppearanceLightStatusBars = false
        onDispose {
            setGround(null)
            controller?.isAppearanceLightStatusBars = !darkTheme
        }
    }
}
