package app.zazi.ui.design

import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalContext

/**
 * Whether the system has asked for less movement.
 *
 * <p>Shared rather than per-screen: "remove animations" is an accessibility setting, and a
 * setting that only some screens honour is worse than one nothing honours, because the user
 * cannot tell which parts of the product they can trust.</p>
 */
@Composable
fun rememberReducedMotion(): Boolean {
    val context = LocalContext.current

    return remember(context) {
        android.provider.Settings.Global.getFloat(
            context.contentResolver,
            android.provider.Settings.Global.ANIMATOR_DURATION_SCALE,
            1f
        ) == 0f
    }
}
