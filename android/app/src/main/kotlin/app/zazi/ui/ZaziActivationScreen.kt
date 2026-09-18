package app.zazi.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.zazi.ui.brand.ZaziWordmark
import app.zazi.ui.design.ErrorNotice
import app.zazi.ui.design.Radius
import app.zazi.ui.design.Spacing
import app.zazi.ui.design.ZaziPanel
import app.zazi.ui.design.ZaziPrimaryButton
import app.zazi.ui.state.ActivationUiState

/**
 * First run, for a worker.
 *
 * <p>One field. A worker is given a code by the person who employs them, types it in, and is
 * working. There is no email, no password and no registration, because a worker operating a
 * business phone never wanted an account — the account belongs to the business, and this
 * screen exists so they do not have to hold one to do their job.</p>
 *
 * <p>Sign-in with an email remains reachable below, unchanged, for owners and managers whose
 * accounts predate activation. It is deliberately quiet: offering two equal choices would
 * make every worker stop and decide which one they are.</p>
 */
@Composable
fun ActivationScreen(
    state: ActivationUiState,
    onCodeChanged: (String) -> Unit,
    onSubmit: () -> Unit,
    onUseEmailInstead: () -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = Spacing.medium),
        verticalArrangement = Arrangement.Center
    ) {
        Spacer(Modifier.height(Spacing.section))

        ZaziWordmark()

        Spacer(Modifier.height(Spacing.small))

        Text(
            "Activate your Zazi access",
            style = MaterialTheme.typography.titleMedium,
            fontWeight = FontWeight.SemiBold,
            color = MaterialTheme.colorScheme.onBackground
        )

        Spacer(Modifier.height(Spacing.tight))

        Text(
            "Enter the activation code provided by your business owner.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )

        Spacer(Modifier.height(Spacing.large))

        OutlinedTextField(
            value = state.code,
            onValueChange = onCodeChanged,
            label = { Text("Activation code") },
            placeholder = { Text("ZAZI-XXXX-XXXX-XXXX-XXXX") },
            singleLine = true,
            enabled = !state.isSubmitting,
            isError = state.error != null,
            // Codes are uppercase Crockford base32 and contain no lowercase letters, so
            // capitalising saves the worker a shift on every character. The server normalises
            // anyway; this is about the typing, not the parsing.
            keyboardOptions = KeyboardOptions(
                keyboardType = KeyboardType.Ascii,
                capitalization = KeyboardCapitalization.Characters
            ),
            shape = MaterialTheme.shapes.medium,
            modifier = Modifier.fillMaxWidth()
        )

        if (state.error != null) {
            Spacer(Modifier.height(Spacing.small))
            ErrorNotice(state.error.message)
        }

        Spacer(Modifier.height(Spacing.large))

        ZaziPrimaryButton(
            text = "Activate Zazi",
            onClick = onSubmit,
            enabled = state.canSubmit,
            busy = state.isSubmitting
        )

        Spacer(Modifier.height(Spacing.medium))

        Text(
            // Stated plainly rather than discovered on a bus with no signal. Activation is
            // the one thing in Zazi that genuinely cannot work offline: only the server can
            // say who this worker is.
            "Activation needs an internet connection. After this, Zazi keeps working offline.",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
            modifier = Modifier.fillMaxWidth()
        )

        Spacer(Modifier.height(Spacing.section))

        TextButton(
            onClick = onUseEmailInstead,
            enabled = !state.isSubmitting,
            modifier = Modifier.fillMaxWidth()
        ) {
            Text("I have a Zazi account — sign in instead")
        }

        Spacer(Modifier.height(Spacing.section))
    }
}

/**
 * Confirmation, immediately after activation.
 *
 * <p>Every name here came from the server. The handset had no idea who its worker was a
 * second ago, and showing anything it composed itself would be a guess presented as fact.</p>
 */
@Composable
fun ActivatedScreen(
    workerName: String,
    organizationName: String,
    branchName: String,
    onContinue: () -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = Spacing.medium),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Spacer(Modifier.height(Spacing.section))

        ZaziWordmark()

        Spacer(Modifier.height(Spacing.large))

        Text(
            "You're connected.",
            style = MaterialTheme.typography.headlineSmall,
            fontWeight = FontWeight.Bold,
            color = MaterialTheme.colorScheme.onBackground
        )

        Spacer(Modifier.height(Spacing.large))

        ZaziPanel(modifier = Modifier.fillMaxWidth()) {
            Column(modifier = Modifier.padding(Spacing.medium)) {
                IdentityRow("You", workerName)
                Spacer(Modifier.height(Spacing.small))
                IdentityRow("Business", organizationName)
                Spacer(Modifier.height(Spacing.small))
                IdentityRow("Branch", branchName)
            }
        }

        Spacer(Modifier.height(Spacing.large))

        ZaziPrimaryButton(text = "Start using Zazi", onClick = onContinue)

        Spacer(Modifier.height(Spacing.section))
    }
}

@Composable
private fun IdentityRow(label: String, value: String) {
    Column {
        Text(
            label,
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        Text(
            // Never capped to one line: a business or a person's name is not the app's to
            // truncate, and a worker seeing a shortened version of their own employer reads
            // as the product not knowing who they are.
            value.ifBlank { "—" },
            style = MaterialTheme.typography.titleMedium,
            fontWeight = FontWeight.SemiBold,
            color = MaterialTheme.colorScheme.onSurface
        )
    }
}
