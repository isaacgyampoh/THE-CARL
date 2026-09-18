package app.zazi.ui

import androidx.compose.animation.core.LinearOutSlowInEasing
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.scale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import app.zazi.ui.brand.ZaziWordmark
import app.zazi.ui.design.ErrorNotice
import app.zazi.ui.design.Radius
import app.zazi.ui.design.Spacing
import app.zazi.ui.design.ZaziPrimaryButton
import app.zazi.ui.state.LoginUiState

/**
 * The front door.
 *
 * <p>Composed as brand, purpose, credentials, action — in that order — so the first thing an
 * agent sees is whose software this is and what it is for, not a pair of empty boxes.</p>
 *
 * <p><b>Credentials, not a PIN.</b> The product authenticates an email and password against
 * the server's own user store, and there is no PIN anywhere in the domain, the client or the
 * API. A PIN pad here would either be decoration over the real fields or a new authentication
 * mechanism, and authentication is not something a visual pass may invent.</p>
 */
@Composable
fun LoginScreen(
    state: LoginUiState,
    onEmailChanged: (String) -> Unit,
    onPasswordChanged: (String) -> Unit,
    onSubmit: () -> Unit
) {
    var passwordVisible by remember { mutableStateOf(false) }

    // The entrance: a short fade with a small rise and settle. It runs once, finishes well
    // inside half a second, and never repeats — the point is that the app feels composed when
    // it opens, not that anything is being performed at the user.
    var entered by remember { mutableStateOf(false) }
    val reducedMotion = rememberReducedMotion()

    LaunchedEffect(Unit) { entered = true }

    val progress by animateFloatAsState(
        targetValue = if (entered) 1f else 0f,
        animationSpec = tween(
            // Honour the system setting rather than deciding for someone that a little
            // movement is harmless: for some people it is not.
            durationMillis = if (reducedMotion) 0 else 420,
            easing = LinearOutSlowInEasing
        ),
        label = "login-entrance"
    )

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 24.dp),
        // Centred when the content fits, scrolled from the top when it does not — which is
        // what happens in landscape, on a small screen and at a large font scale.
        verticalArrangement = Arrangement.Center
    ) {
        // Fixed leading space rather than a weight. A weighted spacer inside a scrolling
        // column needs a height to divide, and the 640dp floor that gave it was a number
        // invented for one phone: in landscape, on a small screen, or at a large font scale
        // it forced a scroll through empty space before the form appeared.
        Spacer(Modifier.height(Spacing.section))

        Column(
            modifier = Modifier
                .alpha(progress)
                // Small enough to feel like settling rather than zooming.
                .scale(0.96f + 0.04f * progress)
        ) {
            ZaziWordmark()

            Spacer(Modifier.height(Spacing.small))

            Text(
                // What the product does, in the words an agent would use. No superlatives:
                // a claim this screen cannot stand behind would be the wrong first thing to
                // say to someone trusting it with their day's takings.
                "Your simple way to keep track of every transaction.",
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }

        Spacer(Modifier.height(Spacing.section))

        OutlinedTextField(
            value = state.email,
            onValueChange = onEmailChanged,
            label = { Text("Email") },
            singleLine = true,
            shape = Radius.control,
            enabled = !state.isSubmitting,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email),
            modifier = Modifier.fillMaxWidth()
        )

        Spacer(Modifier.height(Spacing.small))

        OutlinedTextField(
            value = state.password,
            onValueChange = onPasswordChanged,
            label = { Text("Password") },
            singleLine = true,
            shape = Radius.control,
            enabled = !state.isSubmitting,
            // The value is masked by default and never logged, stored or carried out of this
            // screen; revealing it is an explicit, reversible choice by the person holding
            // the phone, which matters when they are standing at a counter.
            visualTransformation = if (passwordVisible) {
                VisualTransformation.None
            } else {
                PasswordVisualTransformation()
            },
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
            trailingIcon = {
                TextButton(onClick = { passwordVisible = !passwordVisible }) {
                    Text(if (passwordVisible) "Hide" else "Show")
                }
            },
            modifier = Modifier.fillMaxWidth()
        )

        state.error?.let { error ->
            Spacer(Modifier.height(Spacing.small))
            ErrorNotice(error.message)
        }

        Spacer(Modifier.height(Spacing.large))

        ZaziPrimaryButton(
            text = "Sign in",
            onClick = onSubmit,
            enabled = state.canSubmit,
            busy = state.isSubmitting
        )

        Spacer(Modifier.height(Spacing.section))
    }
}

/** Whether the system has asked for less movement. */
@Composable
private fun rememberReducedMotion(): Boolean {
    val context = LocalContext.current

    return remember(context) {
        android.provider.Settings.Global.getFloat(
            context.contentResolver,
            android.provider.Settings.Global.ANIMATOR_DURATION_SCALE,
            1f
        ) == 0f
    }
}
