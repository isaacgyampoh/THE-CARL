package app.thecarl.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import app.thecarl.ui.state.CaptureProvider
import app.thecarl.ui.state.CaptureTransactionType
import app.thecarl.ui.state.CaptureUiState
import app.thecarl.ui.state.DashboardUiState
import app.thecarl.ui.state.EnrolmentUiState
import app.thecarl.ui.state.LoginUiState
import app.thecarl.ui.state.MoneyFormat

/**
 * Screens for the first usable build.
 *
 * <p>Deliberately plain. Every composable here reads a state object produced by a view model
 * and emits events back — no financial logic, no direction calculation, no formatting of
 * money beyond [MoneyFormat]. Functionality before decoration, as this phase intends.</p>
 */

@Composable
fun LoginScreen(
    state: LoginUiState,
    onEmailChanged: (String) -> Unit,
    onPasswordChanged: (String) -> Unit,
    onSubmit: () -> Unit
) {
    var passwordVisible by remember { mutableStateOf(false) }

    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.Center
    ) {
        Text("THE CARL", style = MaterialTheme.typography.headlineMedium)
        Text(
            "Sign in to your account",
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.padding(top = 4.dp, bottom = 24.dp)
        )

        OutlinedTextField(
            value = state.email,
            onValueChange = onEmailChanged,
            label = { Text("Email") },
            singleLine = true,
            enabled = !state.isSubmitting,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email),
            modifier = Modifier.fillMaxWidth()
        )

        Spacer(Modifier.height(12.dp))

        OutlinedTextField(
            value = state.password,
            onValueChange = onPasswordChanged,
            label = { Text("Password") },
            singleLine = true,
            enabled = !state.isSubmitting,
            // Masked by default. The toggle exists because a mistyped password on a small
            // keyboard is the commonest sign-in failure.
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
            Spacer(Modifier.height(12.dp))
            Text(
                error.message,
                color = MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.bodyMedium
            )
        }

        Spacer(Modifier.height(24.dp))

        Button(
            onClick = onSubmit,
            // Disabled while in flight so a double tap cannot send two login requests.
            enabled = state.canSubmit,
            modifier = Modifier.fillMaxWidth().height(52.dp)
        ) {
            if (state.isSubmitting) {
                CircularProgressIndicator(modifier = Modifier.height(20.dp))
            } else {
                Text("Sign in")
            }
        }
    }
}

@Composable
fun EnrolmentScreen(
    state: EnrolmentUiState,
    onCodeChanged: (String) -> Unit,
    onSubmit: () -> Unit
) {
    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.Center
    ) {
        Text("Register this device", style = MaterialTheme.typography.headlineSmall)
        Text(
            "Enter the enrolment code from your manager. Your branch and permissions are " +
                "set by that code.",
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.padding(top = 8.dp, bottom = 24.dp)
        )

        OutlinedTextField(
            value = state.code,
            onValueChange = onCodeChanged,
            label = { Text("Enrolment code") },
            placeholder = { Text("CARL-XXXX-XXXX-…") },
            singleLine = true,
            enabled = !state.isSubmitting,
            modifier = Modifier.fillMaxWidth()
        )

        state.error?.let { error ->
            Spacer(Modifier.height(12.dp))
            Text(
                error.message,
                color = MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.bodyMedium
            )
        }

        Spacer(Modifier.height(24.dp))

        Button(
            onClick = onSubmit,
            enabled = state.canSubmit,
            modifier = Modifier.fillMaxWidth().height(52.dp)
        ) {
            if (state.isSubmitting) {
                CircularProgressIndicator(modifier = Modifier.height(20.dp))
            } else {
                Text("Register device")
            }
        }
    }
}

@Composable
fun DashboardScreen(
    state: DashboardUiState,
    onCapture: () -> Unit,
    onSyncNow: () -> Unit,
    onLogout: () -> Unit
) {
    Column(
        modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(20.dp)
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text("Today", style = MaterialTheme.typography.headlineSmall)
            Text(
                if (state.isOnline) "Online" else "Offline",
                style = MaterialTheme.typography.labelMedium
            )
        }

        Spacer(Modifier.height(16.dp))

        // Absent rather than zero when a total cannot be calculated. A fabricated figure on
        // a financial dashboard is worse than an empty one.
        Card(modifier = Modifier.fillMaxWidth()) {
            Column(Modifier.padding(16.dp)) {
                Text("Cash movement", style = MaterialTheme.typography.labelMedium)
                Text(
                    state.todayCashMinor?.let { MoneyFormat.format(it) } ?: "—",
                    style = MaterialTheme.typography.headlineMedium
                )
                Spacer(Modifier.height(8.dp))
                Text("Float movement", style = MaterialTheme.typography.labelMedium)
                Text(
                    state.todayFloatMinor?.let { MoneyFormat.format(it) } ?: "—",
                    style = MaterialTheme.typography.titleLarge
                )
            }
        }

        Spacer(Modifier.height(16.dp))

        Card(modifier = Modifier.fillMaxWidth()) {
            Column(Modifier.padding(16.dp)) {
                Text("Synchronisation", style = MaterialTheme.typography.titleMedium)
                Spacer(Modifier.height(8.dp))

                SyncRow("Waiting to sync", state.pendingCount)
                SyncRow("Syncing now", state.syncingCount)
                SyncRow("Retrying", state.retryingCount)
                SyncRow("Synced today", state.syncedTodayCount)

                if (state.needsAttentionCount > 0) {
                    Spacer(Modifier.height(8.dp))
                    // Never auto-resolved and never hidden.
                    Text(
                        "${state.needsAttentionCount} need review",
                        color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodyMedium
                    )
                }

                Spacer(Modifier.height(12.dp))
                TextButton(onClick = onSyncNow, enabled = state.unsyncedCount > 0) {
                    Text("Sync now")
                }
            }
        }

        Spacer(Modifier.height(24.dp))

        Button(
            onClick = onCapture,
            modifier = Modifier.fillMaxWidth().height(56.dp)
        ) {
            Text("Record transaction")
        }

        Spacer(Modifier.height(24.dp))

        state.device?.let { device ->
            Text("Branch ${device.branchId}", style = MaterialTheme.typography.bodySmall)
            Text(
                if (device.canAttemptSmsCapture) {
                    // Capability, not a granted permission — the runtime prompt is separate.
                    "Automatic SMS capture available on this device"
                } else {
                    "Manual transaction capture"
                },
                style = MaterialTheme.typography.bodySmall
            )
        }

        Spacer(Modifier.height(16.dp))
        TextButton(onClick = onLogout) { Text("Sign out") }
    }
}

@Composable
private fun SyncRow(label: String, count: Int) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp),
        horizontalArrangement = Arrangement.SpaceBetween
    ) {
        Text(label, style = MaterialTheme.typography.bodyMedium)
        Text("$count", style = MaterialTheme.typography.bodyMedium)
    }
}

@Composable
fun CaptureScreen(
    state: CaptureUiState,
    onTypeChanged: (CaptureTransactionType) -> Unit,
    onProviderChanged: (CaptureProvider) -> Unit,
    onAmountChanged: (String) -> Unit,
    onCustomerPhoneChanged: (String) -> Unit,
    onReferenceChanged: (String) -> Unit,
    onSubmit: () -> Unit,
    onDone: () -> Unit
) {
    Column(
        modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(20.dp)
    ) {
        Text("Record transaction", style = MaterialTheme.typography.headlineSmall)
        Spacer(Modifier.height(16.dp))

        // The four type chips are wider than the screen, which broke "Commission" across two
        // lines inside its own chip. Scrolling keeps every option reachable and each label on
        // one line, without dropping or abbreviating any transaction type.
        Row(
            modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            CaptureTransactionType.entries.forEach { type ->
                FilterChip(
                    selected = state.transactionType == type,
                    onClick = { onTypeChanged(type) },
                    label = { Text(type.label, maxLines = 1) }
                )
            }
        }

        Spacer(Modifier.height(12.dp))

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            CaptureProvider.entries.forEach { provider ->
                FilterChip(
                    selected = state.provider == provider,
                    onClick = { onProviderChanged(provider) },
                    label = { Text(provider.label) }
                )
            }
        }

        Spacer(Modifier.height(16.dp))

        OutlinedTextField(
            value = state.amountInput,
            onValueChange = onAmountChanged,
            label = { Text("Amount (GHS)") },
            prefix = { Text("₵") },
            singleLine = true,
            enabled = !state.isSubmitting,
            // Decimal, not number: pesewas matter and the domain rejects rounding.
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
            modifier = Modifier.fillMaxWidth()
        )

        Spacer(Modifier.height(12.dp))

        OutlinedTextField(
            value = state.customerPhone,
            onValueChange = onCustomerPhoneChanged,
            label = { Text("Customer number (optional)") },
            singleLine = true,
            enabled = !state.isSubmitting,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Phone),
            modifier = Modifier.fillMaxWidth()
        )

        Spacer(Modifier.height(12.dp))

        OutlinedTextField(
            value = state.reference,
            onValueChange = onReferenceChanged,
            label = { Text("Provider reference (optional)") },
            singleLine = true,
            enabled = !state.isSubmitting,
            modifier = Modifier.fillMaxWidth()
        )

        state.error?.let { error ->
            Spacer(Modifier.height(12.dp))
            Text(
                error.message,
                color = MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.bodyMedium
            )
        }

        state.lastResult?.let { confirmation ->
            Spacer(Modifier.height(16.dp))
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp)) {
                    // Says only what is true: it is on this device. Server acceptance is a
                    // separate fact the sync status reports later.
                    Text(confirmation.headline, style = MaterialTheme.typography.titleMedium)
                    Text(
                        "${confirmation.transactionType.label} " +
                            MoneyFormat.format(confirmation.amountMinor),
                        style = MaterialTheme.typography.bodyLarge
                    )
                    Text(confirmation.syncMessage, style = MaterialTheme.typography.bodyMedium)
                    Text(
                        "Reference ${confirmation.shortReference}",
                        style = MaterialTheme.typography.bodySmall
                    )
                    Spacer(Modifier.height(8.dp))
                    TextButton(onClick = onDone) { Text("Done") }
                }
            }
        }

        Spacer(Modifier.height(24.dp))

        Button(
            onClick = onSubmit,
            enabled = state.canSubmit,
            modifier = Modifier.fillMaxWidth().height(56.dp)
        ) {
            if (state.isSubmitting) {
                CircularProgressIndicator(modifier = Modifier.height(20.dp))
            } else {
                Text("Save transaction")
            }
        }
    }
}

/** Shown when the server no longer trusts this device. */
@Composable
fun DeviceRevokedScreen(queuedWorkCount: Int, onSignIn: () -> Unit) {
    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.Center
    ) {
        Text("Device access revoked", style = MaterialTheme.typography.headlineSmall)
        Spacer(Modifier.height(12.dp))

        Text(
            if (queuedWorkCount > 0) {
                // Stated explicitly. An agent whose device is cut off needs to know their
                // work is safe, not wonder whether it was discarded.
                "$queuedWorkCount transaction${if (queuedWorkCount == 1) "" else "s"} " +
                    "recorded on this device are still stored safely and will sync once " +
                    "access is restored."
            } else {
                "This device is no longer registered. Ask your manager to register it again."
            },
            style = MaterialTheme.typography.bodyMedium
        )

        Spacer(Modifier.height(24.dp))
        Button(onClick = onSignIn, modifier = Modifier.fillMaxWidth().height(52.dp)) {
            Text("Sign in again")
        }
    }
}

@Composable
fun LoadingScreen() {
    Column(
        modifier = Modifier.fillMaxSize(),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        CircularProgressIndicator()
    }
}
