package app.zazi.ui.brand

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import app.zazi.ui.theme.StatusBarGround

/**
 * The brand band at the top of the sign-in and activation screens: flat navy, the same ground
 * as the portal's sign-in panel and the dashboard's balance header. No pattern, no gradient —
 * a money product's identity is its restraint.
 */
@Composable
fun BrandBand(modifier: Modifier = Modifier, content: @Composable BoxScope.() -> Unit) {
    StatusBarGround(BrandColours.navy)
    Box(modifier.background(BrandColours.navy), content = content)
}

/** Colours for words set on the band. */
object BrandOn {
    val Text = Color(0xFFF4F7FB)
    val Muted = Color(0xFFA9B8C9)
    val Accent = BrandColours.gold
}
