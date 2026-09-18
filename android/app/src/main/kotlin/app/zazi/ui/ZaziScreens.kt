package app.zazi.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.VerticalDivider
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardCapitalization
import app.zazi.ui.state.EnrolmentError
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import app.zazi.ui.state.CaptureConfirmation
import app.zazi.ui.state.CaptureError
import app.zazi.ui.state.CaptureProvider
import app.zazi.ui.state.CaptureTransactionType
import app.zazi.ui.state.CaptureUiState
import app.zazi.ui.state.DashboardUiState
import app.zazi.ui.state.EnrolmentUiState
import app.zazi.ui.state.LoginUiState
import app.zazi.ui.state.MoneyFormat

/**
 * Screens for the first usable build.
 *
 * <p>Deliberately plain. Every composable here reads a state object produced by a view model
 * and emits events back — no financial logic, no direction calculation, no formatting of
 * money beyond [MoneyFormat]. Functionality before decoration, as this phase intends.</p>
 */


/**
 * A failure, presented the same way on every screen.
 *
 * <p>Error text used to be a bare red line on some screens and a tinted panel on others, so
 * the same kind of event looked like two different kinds of event.</p>
 */
@Composable
private fun ErrorNotice(message: String) {
    Spacer(Modifier.height(12.dp))
    Surface(
        color = MaterialTheme.colorScheme.errorContainer,
        shape = MaterialTheme.shapes.small,
        modifier = Modifier.fillMaxWidth()
    ) {
        Text(
            message,
            color = MaterialTheme.colorScheme.onErrorContainer,
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.padding(horizontal = 12.dp, vertical = 10.dp)
        )
    }
}

@Composable
fun LoginScreen(
    state: LoginUiState,
    onEmailChanged: (String) -> Unit,
    onPasswordChanged: (String) -> Unit,
    onSubmit: () -> Unit
) {
    var passwordVisible by remember { mutableStateOf(false) }

    // Scrollable, and centred only when there is room to be. With the keyboard open on a
    // short handset the submit button was drawn underneath it and could not be reached —
    // the form had no scroll of its own and nothing reserved space for the IME. Center
    // still applies when the content is shorter than the screen.
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(24.dp),
        verticalArrangement = Arrangement.Center
    ) {
        Text("Zazi", style = MaterialTheme.typography.headlineMedium)
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

        state.error?.let { error -> ErrorNotice(error.message) }

        Spacer(Modifier.height(24.dp))

        Button(
            onClick = onSubmit,
            // Disabled while in flight so a double tap cannot send two login requests.
            enabled = state.canSubmit,
            modifier = Modifier.fillMaxWidth().height(52.dp)
        ) {
            if (state.isSubmitting) {
                CircularProgressIndicator(
                    modifier = Modifier.size(20.dp),
                    color = MaterialTheme.colorScheme.onPrimary,
                    strokeWidth = 2.dp
                )
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
    // Scrollable, and centred only when there is room to be. With the keyboard open on a
    // short handset the submit button was drawn underneath it and could not be reached —
    // the form had no scroll of its own and nothing reserved space for the IME. Center
    // still applies when the content is shorter than the screen.
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(24.dp),
        verticalArrangement = Arrangement.Center
    ) {
        Text("Register this device", style = MaterialTheme.typography.headlineSmall)
        Text(
            "Enter the enrolment code from your manager. Your branch and permissions are " +
                "set by that code.",
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.padding(top = 8.dp, bottom = 24.dp)
        )

        // The code is fifty-odd characters and is read aloud or copied from a message, so
        // this field is built for checking work rather than for brevity:
        //
        //  - not singleLine, because on one line an agent only ever sees the tail and cannot
        //    compare what they typed against what they were given;
        //  - monospace, so the groups align and O/0 and I/1 are told apart;
        //  - forced uppercase as they type, so it matches the code on the page. The server
        //    normalises case anyway, but a field that looks wrong invites a retype;
        //  - autocorrect off. A keyboard "helpfully" rewriting a group is invisible until
        //    enrolment fails, and the agent has no way to tell what happened.
        OutlinedTextField(
            value = state.code,
            onValueChange = { onCodeChanged(it.uppercase()) },
            label = { Text("Enrolment code") },
            placeholder = { Text("ZAZI-XXXX-XXXX-…") },
            textStyle = LocalTextStyle.current.copy(fontFamily = FontFamily.Monospace),
            minLines = 2,
            maxLines = 3,
            enabled = !state.isSubmitting,
            isError = state.error == EnrolmentError.INVALID_OR_EXPIRED,
            keyboardOptions = KeyboardOptions(
                capitalization = KeyboardCapitalization.Characters,
                autoCorrect = false,
                keyboardType = KeyboardType.Ascii
            ),
            modifier = Modifier.fillMaxWidth()
        )

        state.error?.let { error -> ErrorNotice(error.message) }

        Spacer(Modifier.height(24.dp))

        Button(
            onClick = onSubmit,
            enabled = state.canSubmit,
            modifier = Modifier.fillMaxWidth().height(52.dp)
        ) {
            if (state.isSubmitting) {
                CircularProgressIndicator(
                    modifier = Modifier.size(20.dp),
                    color = MaterialTheme.colorScheme.onPrimary,
                    strokeWidth = 2.dp
                )
            } else {
                Text("Register device")
            }
        }
    }
}

/**
 * The screen an agent looks at between customers.
 *
 * <p>Laid out around the two questions actually being asked — "where do I stand today?" and
 * "is anything stuck?" — with the action performed dozens of times a day anchored to the
 * bottom so it is always under a thumb and never behind a scroll.</p>
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DashboardScreen(
    state: DashboardUiState,
    onCapture: () -> Unit,
    onSyncNow: () -> Unit,
    onLogout: () -> Unit,
    /** Android runtime state, deliberately separate from the server's device capability. */
    smsPermissionGranted: Boolean = false,
    onRequestSmsPermission: () -> Unit = {}
) {
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Today") },
                actions = {
                    ConnectionChip(isOnline = state.isOnline)
                    Spacer(Modifier.width(4.dp))
                    TextButton(onClick = onLogout) { Text("Sign out") }
                }
            )
        },
        bottomBar = {
            // Anchored rather than placed in the scroll. Recording a transaction is the
            // whole point of the screen, and it used to sit mid-page where it moved as the
            // sync list grew and could be scrolled off entirely.
            Surface(tonalElevation = 3.dp) {
                Button(
                    onClick = onCapture,
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 20.dp, vertical = 12.dp)
                        .height(56.dp)
                ) {
                    Text("Record transaction", style = MaterialTheme.typography.titleMedium)
                }
            }
        }
    ) { insets ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(insets)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 20.dp)
        ) {
            Spacer(Modifier.height(4.dp))

            MovementCard(
                cashMinor = state.todayCashMinor,
                floatMinor = state.todayFloatMinor
            )

            Spacer(Modifier.height(12.dp))

            SyncCard(state = state, onSyncNow = onSyncNow)

            state.device?.let { device ->
                Spacer(Modifier.height(12.dp))

                // Two different facts, deliberately not collapsed into one line. The server
                // says whether this kind of device may capture SMS at all; Android says
                // whether this installation has been allowed to. Showing only the first
                // would tell an agent capture is running when no message can reach the app.
                when {
                    !device.canAttemptSmsCapture ->
                        CaptureModeRow("Manual capture", "Transactions are recorded by hand.")

                    smsPermissionGranted ->
                        CaptureModeRow(
                            "Automatic capture is on",
                            "Mobile-money alerts are recorded as they arrive."
                        )

                    else -> SmsPermissionCard(onRequestSmsPermission)
                }

                Spacer(Modifier.height(16.dp))

                // The branch name when the server has sent one. It falls back to a short
                // reference offline, and on servers predating the field — never the full
                // UUID, which told an agent nothing and took a line and a half doing it.
                Text(
                    device.branchName ?: "Branch ref ${device.branchId.take(8)}",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }

            Spacer(Modifier.height(16.dp))
        }
    }
}

/** Online state as something glanceable, rather than a word among other words. */
@Composable
private fun ConnectionChip(isOnline: Boolean) {
    val colours = MaterialTheme.colorScheme
    val tint = if (isOnline) colours.primary else colours.onSurfaceVariant

    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(
            modifier = Modifier
                .size(8.dp)
                .background(tint, CircleShape)
        )
        Spacer(Modifier.width(6.dp))
        Text(
            if (isOnline) "Online" else "Offline",
            style = MaterialTheme.typography.labelLarge,
            color = tint
        )
    }
}

/**
 * Today's movement.
 *
 * <p>Absent rather than zero when a total cannot be calculated. A fabricated figure on a
 * financial dashboard is worse than an empty one.</p>
 */
@Composable
private fun MovementCard(cashMinor: Long?, floatMinor: Long?) {
    ElevatedCard(modifier = Modifier.fillMaxWidth()) {
        Row(modifier = Modifier.fillMaxWidth().padding(20.dp)) {
            MovementFigure(
                label = "Cash",
                minor = cashMinor,
                modifier = Modifier.weight(1f)
            )
            // A hairline rather than a gap: the two figures are read together and move in
            // opposite directions, so they need to look like one statement, not two cards.
            VerticalDivider(
                modifier = Modifier.height(56.dp),
                color = MaterialTheme.colorScheme.outlineVariant
            )
            MovementFigure(
                label = "Float",
                minor = floatMinor,
                modifier = Modifier.weight(1f).padding(start = 16.dp)
            )
        }
    }
}

@Composable
private fun MovementFigure(label: String, minor: Long?, modifier: Modifier = Modifier) {
    Column(modifier = modifier) {
        Text(
            label,
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        Spacer(Modifier.height(4.dp))
        Text(
            minor?.let { MoneyFormat.format(it) } ?: "—",
            style = MaterialTheme.typography.headlineSmall,
            maxLines = 1
        )
    }
}

/**
 * Sync state, summarised.
 *
 * <p>Previously four rows that read "0" all day. The counts only matter when they are not
 * zero, so the ordinary case is now a single settled line and the numbers appear when there
 * is something to say about them.</p>
 */
@Composable
private fun SyncCard(state: DashboardUiState, onSyncNow: () -> Unit) {
    Card(modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(20.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(
                        when {
                            state.unsyncedCount > 0 -> "${state.unsyncedCount} waiting to sync"
                            state.syncedTodayCount > 0 -> "All synced"
                            else -> "Nothing recorded yet"
                        },
                        style = MaterialTheme.typography.titleMedium
                    )
                    Spacer(Modifier.height(2.dp))
                    Text(
                        when {
                            state.unsyncedCount > 0 && !state.isOnline ->
                                "Saved on this device. They will send when you are back online."
                            state.unsyncedCount > 0 -> "Sending automatically."
                            state.syncedTodayCount > 0 ->
                                "${state.syncedTodayCount} sent today."
                            else -> "Recorded transactions appear here."
                        },
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }

                if (state.unsyncedCount > 0) {
                    TextButton(onClick = onSyncNow) { Text("Sync now") }
                }
            }

            // Never auto-resolved and never hidden. Given its own tone because it is the one
            // thing on this screen that needs a person rather than time.
            if (state.needsAttentionCount > 0) {
                Spacer(Modifier.height(12.dp))
                Surface(
                    color = MaterialTheme.colorScheme.errorContainer,
                    shape = MaterialTheme.shapes.small,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text(
                        "${state.needsAttentionCount} need review",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onErrorContainer,
                        modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp)
                    )
                }
            }
        }
    }
}

@Composable
private fun CaptureModeRow(title: String, detail: String) {
    Column {
        Text(title, style = MaterialTheme.typography.bodyMedium)
        Text(
            detail,
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

/**
 * The SMS permission ask.
 *
 * <p>Kept as a card with one action. It was four paragraphs of prose that dominated the
 * screen and buried the choice; the explanation an agent needs before granting access to
 * their messages is short, and the reassurance that declining is fine belongs next to the
 * button rather than three lines below it.</p>
 */
@Composable
private fun SmsPermissionCard(onRequest: () -> Unit) {
    Card(modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(20.dp)) {
            Text("Record transactions automatically", style = MaterialTheme.typography.titleMedium)
            Spacer(Modifier.height(6.dp))
            Text(
                "Zazi can read incoming mobile-money alerts so you do not have to type them. " +
                    "It never reads your other messages and never sends any.",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Spacer(Modifier.height(12.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Button(onClick = onRequest) { Text("Allow") }
                Spacer(Modifier.width(12.dp))
                Text(
                    "Recording by hand keeps working.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
    }
}

/**
 * Manual capture.
 *
 * <p>Ordered by how much each answer matters. Direction first, because cash in and cash out
 * move the ledger opposite ways and picking the wrong one is the costliest mistake available
 * on this screen; then the amount, large enough to check at a glance before saving; then the
 * optional details, plainly marked so nobody types them out of obligation.</p>
 */
@OptIn(ExperimentalMaterial3Api::class)
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
    // While a confirmation is showing, the form is done with. Keeping the Save button live
    // underneath it invited a second identical capture.
    val confirmation = state.lastResult

    Scaffold(
        topBar = { TopAppBar(title = { Text("Record transaction") }) },
        bottomBar = {
            if (confirmation == null) {
                Surface(tonalElevation = 3.dp) {
                    Button(
                        onClick = onSubmit,
                        enabled = state.canSubmit,
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 20.dp, vertical = 12.dp)
                            .height(56.dp)
                    ) {
                        if (state.isSubmitting) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(20.dp),
                                color = MaterialTheme.colorScheme.onPrimary,
                                strokeWidth = 2.dp
                            )
                        } else {
                            Text("Save transaction", style = MaterialTheme.typography.titleMedium)
                        }
                    }
                }
            }
        }
    ) { insets ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(insets)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 20.dp)
        ) {
            if (confirmation != null) {
                Spacer(Modifier.height(8.dp))
                ConfirmationCard(confirmation, onDone)
                Spacer(Modifier.height(16.dp))
                return@Column
            }

            Spacer(Modifier.height(4.dp))
            FieldLabel("Direction")

            // The four type chips are wider than the screen, which broke "Commission" across
            // two lines inside its own chip. Scrolling keeps every option reachable and each
            // label on one line, without dropping or abbreviating any transaction type.
            Row(
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                CaptureTransactionType.entries.forEach { type ->
                    val selected = state.transactionType == type
                    FilterChip(
                        selected = selected,
                        onClick = { onTypeChanged(type) },
                        label = { Text(type.label, maxLines = 1) },
                        // Colour alone carried the selection, which is a poor signal for the
                        // one choice on this screen that must not be misread — and no signal
                        // at all for someone who cannot distinguish the two tints.
                        leadingIcon = if (selected) {
                            { Icon(Icons.Filled.Check, contentDescription = null, modifier = Modifier.size(18.dp)) }
                        } else {
                            null
                        }
                    )
                }
            }

            Spacer(Modifier.height(20.dp))
            FieldLabel("Amount")

            OutlinedTextField(
                value = state.amountInput,
                onValueChange = onAmountChanged,
                placeholder = { Text("0.00", style = MaterialTheme.typography.headlineSmall) },
                prefix = { Text("₵", style = MaterialTheme.typography.headlineSmall) },
                // Set large deliberately: this is the figure that has to be checked against
                // the customer's handset before saving, and it was the same size as the
                // optional reference field.
                textStyle = MaterialTheme.typography.headlineSmall,
                singleLine = true,
                isError = state.error == CaptureError.INVALID_AMOUNT ||
                    state.error == CaptureError.AMOUNT_TOO_SMALL ||
                    state.error == CaptureError.SUB_PESEWA_PRECISION,
                enabled = !state.isSubmitting,
                // Decimal, not number: pesewas matter and the domain rejects rounding.
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                modifier = Modifier.fillMaxWidth()
            )

            Spacer(Modifier.height(20.dp))
            FieldLabel("Provider")

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                CaptureProvider.entries.forEach { provider ->
                    val selected = state.provider == provider
                    FilterChip(
                        selected = selected,
                        onClick = { onProviderChanged(provider) },
                        label = { Text(provider.label) },
                        leadingIcon = if (selected) {
                            { Icon(Icons.Filled.Check, contentDescription = null, modifier = Modifier.size(18.dp)) }
                        } else {
                            null
                        }
                    )
                }
            }

            Spacer(Modifier.height(20.dp))
            FieldLabel("Optional")

            OutlinedTextField(
                value = state.customerPhone,
                onValueChange = onCustomerPhoneChanged,
                label = { Text("Customer number") },
                singleLine = true,
                enabled = !state.isSubmitting,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Phone),
                modifier = Modifier.fillMaxWidth()
            )

            Spacer(Modifier.height(12.dp))

            OutlinedTextField(
                value = state.reference,
                onValueChange = onReferenceChanged,
                label = { Text("Provider reference") },
                supportingText = {
                    Text("Helps match this to the provider's own record.")
                },
                singleLine = true,
                enabled = !state.isSubmitting,
                modifier = Modifier.fillMaxWidth()
            )

            state.error?.let { error ->
                Spacer(Modifier.height(12.dp))
                Surface(
                    color = MaterialTheme.colorScheme.errorContainer,
                    shape = MaterialTheme.shapes.small,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text(
                        error.message,
                        color = MaterialTheme.colorScheme.onErrorContainer,
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.padding(horizontal = 12.dp, vertical = 10.dp)
                    )
                }
            }

            Spacer(Modifier.height(16.dp))
        }
    }
}

@Composable
private fun FieldLabel(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.labelLarge,
        color = MaterialTheme.colorScheme.onSurfaceVariant
    )
    Spacer(Modifier.height(8.dp))
}

/**
 * What happened, after a capture.
 *
 * <p><b>Saved locally is not the same as accepted by the server.</b> The wording says only
 * what is true — the transaction is on this device and queued — because claiming server
 * acceptance before sync confirms it would be a lie an agent might act on.</p>
 */
@Composable
private fun ConfirmationCard(confirmation: CaptureConfirmation, onDone: () -> Unit) {
    ElevatedCard(modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(20.dp)) {
            Text(confirmation.headline, style = MaterialTheme.typography.titleMedium)

            Spacer(Modifier.height(8.dp))
            Text(
                MoneyFormat.format(confirmation.amountMinor),
                style = MaterialTheme.typography.headlineMedium
            )
            Text(
                confirmation.transactionType.label,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            Spacer(Modifier.height(12.dp))
            Text(confirmation.syncMessage, style = MaterialTheme.typography.bodyMedium)
            Text(
                "Reference ${confirmation.shortReference}",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            Spacer(Modifier.height(16.dp))
            Button(
                onClick = onDone,
                modifier = Modifier.fillMaxWidth().height(52.dp)
            ) {
                Text("Done")
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
            "This device is no longer registered. Contact your manager or administrator to " +
                "register it again.",
            style = MaterialTheme.typography.bodyMedium
        )

        if (queuedWorkCount > 0) {
            Spacer(Modifier.height(16.dp))

            // Stated explicitly and unconditionally. An agent whose device is cut off needs
            // to know their work is safe, not wonder whether it was discarded — and this is
            // the moment they are most likely to assume the worst. Given its own surface so
            // it reads as the reassurance it is, rather than as more of the bad news.
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp)) {
                    Text(
                        "$queuedWorkCount transaction${if (queuedWorkCount == 1) "" else "s"} still saved here",
                        style = MaterialTheme.typography.titleSmall
                    )
                    Spacer(Modifier.height(4.dp))
                    Text(
                        "Nothing has been deleted. They will sync once access is restored.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
        }

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
        Spacer(Modifier.height(16.dp))
        // A bare spinner leaves an agent guessing whether the app is working or stuck.
        Text(
            "Opening Zazi…",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}
