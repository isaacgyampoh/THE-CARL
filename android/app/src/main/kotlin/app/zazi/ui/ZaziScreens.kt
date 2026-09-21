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
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.clickable
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
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.IconButton
import androidx.compose.material3.AlertDialog
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.RadioButton
import app.zazi.core.data.repository.ParsingVerdict
import app.zazi.ui.state.ParsingReportUiState
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
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import app.zazi.ui.design.AnchoredActionBar
import app.zazi.ui.design.DirectionBadge
import app.zazi.ui.design.EmptyState
import app.zazi.ui.design.ErrorNotice
import app.zazi.ui.design.Radius
import app.zazi.ui.design.SectionHeader
import app.zazi.ui.design.SegmentedFilter
import app.zazi.ui.design.Sizing
import app.zazi.ui.design.Spacing
import app.zazi.ui.design.StatusLabel
import app.zazi.ui.design.StatusTone
import app.zazi.ui.design.ZaziPanel
import app.zazi.ui.design.ZaziPrimaryButton
import app.zazi.ui.state.ActivityDelivery
import app.zazi.ui.state.ActivityFilter
import app.zazi.ui.state.ActivityItem
import app.zazi.ui.state.CaptureConfirmation
import app.zazi.ui.state.CaptureError
import app.zazi.ui.state.CaptureProvider
import app.zazi.ui.state.CaptureTransactionType
import app.zazi.ui.state.CaptureUiState
import app.zazi.ui.state.DashboardUiState
import app.zazi.ui.state.EnrolmentUiState
import app.zazi.ui.state.LoginUiState
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import app.zazi.ui.state.TransactionDetail
import app.zazi.ui.state.MoneyFormat

/**
 * Screens for the first usable build.
 *
 * <p>Deliberately plain. Every composable here reads a state object produced by a view model
 * and emits events back — no financial logic, no direction calculation, no formatting of
 * money beyond [MoneyFormat]. Functionality before decoration, as this phase intends.</p>
 */


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
            placeholder = { Text("ZAZI-XXXX-XXXX-XXXX-XXXX-XXXX") },
            textStyle = LocalTextStyle.current.copy(fontFamily = FontFamily.Monospace),
            minLines = 1,
            maxLines = 2,
            enabled = !state.isSubmitting,
            isError = state.error == EnrolmentError.INVALID_OR_EXPIRED,
            keyboardOptions = KeyboardOptions(
                capitalization = KeyboardCapitalization.Characters,
                autoCorrectEnabled = false,
                keyboardType = KeyboardType.Ascii
            ),
            modifier = Modifier.fillMaxWidth()
        )

        state.error?.let { error ->
            Spacer(Modifier.height(Spacing.small))
            ErrorNotice(error.message)
        }

        Spacer(Modifier.height(24.dp))

        Button(
            onClick = onSubmit,
            enabled = state.canSubmit,
            modifier = Modifier.fillMaxWidth().heightIn(min = Sizing.secondaryAction)
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
 * <p>Ordered by the questions actually being asked, in the order they are asked: where do I
 * stand, what has moved today, did the thing I just recorded take, and is anything stuck.
 * The action performed dozens of times a shift is anchored under a thumb.</p>
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DashboardScreen(
    state: DashboardUiState,
    onCapture: () -> Unit,
    onSyncNow: () -> Unit,
    onLogout: () -> Unit,
    onFilterChanged: (ActivityFilter) -> Unit = {},
    onActivitySelected: (ActivityItem) -> Unit = {},
    /** Android runtime state, deliberately separate from the server's device capability. */
    smsPermissionGranted: Boolean = false,
    onRequestSmsPermission: () -> Unit = {},
    /**
     * True when this session began with an activation code. Changes what signing out costs,
     * and therefore what the confirmation is allowed to say.
     */
    isActivationOnly: Boolean = false
) {
    // Signing out is destructive for a code-activated worker in a way it is not for an
    // account holder: there is nothing to sign back into, and getting the phone working again
    // takes their owner revoking the device and issuing a fresh code. One stray tap in the
    // top bar should not cost somebody the rest of their shift.
    var confirmingSignOut by rememberSaveable { mutableStateOf(false) }

    if (confirmingSignOut) {
        AlertDialog(
            onDismissRequest = { confirmingSignOut = false },
            title = { Text(if (isActivationOnly) "Sign out of Zazi?" else "Sign out?") },
            text = {
                Text(
                    if (isActivationOnly) {
                        // Named precisely, because it is not recoverable on the handset alone.
                        "You will need a new activation code from your business owner to use " +
                            "Zazi on this phone again. Anything you have recorded is kept and " +
                            "will sync once you are back."
                    } else {
                        "You can sign back in with your email and password. Anything you have " +
                            "recorded is kept and will sync once you are back."
                    }
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    confirmingSignOut = false
                    onLogout()
                }) { Text("Sign out") }
            },
            dismissButton = {
                TextButton(onClick = { confirmingSignOut = false }) { Text("Stay signed in") }
            }
        )
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Today", style = MaterialTheme.typography.titleLarge) },
                actions = {
                    ConnectionChip(isOnline = state.isOnline)
                    Spacer(Modifier.width(Spacing.tight))
                    TextButton(onClick = { confirmingSignOut = true }) { Text("Sign out") }
                }
            )
        },
        bottomBar = {
            AnchoredActionBar {
                ZaziPrimaryButton(text = "Record transaction", onClick = onCapture)
            }
        }
    ) { insets ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(insets)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = Spacing.large)
        ) {
            PositionPanel(
                cashMinor = state.todayCashMinor,
                floatMinor = state.todayFloatMinor
            )

            Spacer(Modifier.height(Spacing.small))

            DeliverySummary(state = state, onSyncNow = onSyncNow)

            Spacer(Modifier.height(Spacing.section))

            ActivitySection(
                items = state.activity,
                filter = state.activityFilter,
                onFilterChanged = onFilterChanged,
                onSelect = onActivitySelected
            )

            state.device?.let { device ->
                Spacer(Modifier.height(Spacing.section))

                // Two different facts, deliberately not collapsed into one line. The server
                // says whether this kind of device may capture SMS at all; Android says
                // whether this installation has been allowed to. Showing only the first would
                // tell an agent capture is running when no message can reach the app.
                when {
                    !device.canAttemptSmsCapture ->
                        CaptureModeRow("Manual capture", "Transactions are recorded by hand.")

                    smsPermissionGranted ->
                        CaptureModeRow(
                            "Automatic capture is on",
                            "Mobile-money alerts are recorded as they arrive."
                        )

                    else -> SmsPermissionPanel(onRequestSmsPermission)
                }

                Spacer(Modifier.height(Spacing.medium))

                // The branch name when the server has sent one; a short reference offline and
                // on servers predating the field — never the full UUID, which told an agent
                // nothing and took a line and a half doing it.
                Text(
                    device.branchName ?: "Branch ref ${device.branchId.take(8)}",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }

            Spacer(Modifier.height(Spacing.large))
        }
    }
}

/** Online state as something glanceable, rather than a word among other words. */
@Composable
private fun ConnectionChip(isOnline: Boolean) {
    val colours = MaterialTheme.colorScheme
    val tint = if (isOnline) colours.primary else colours.onSurfaceVariant

    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(8.dp).background(tint, CircleShape))
        Spacer(Modifier.width(6.dp))
        Text(
            if (isOnline) "Online" else "Offline",
            style = MaterialTheme.typography.labelLarge,
            color = tint
        )
    }
}

/**
 * Where the agent stands today.
 *
 * <p>The one figure looked for first, so it gets the strongest surface in the palette and the
 * largest type on the screen. Absent rather than zero when a total cannot be calculated: a
 * fabricated figure on a financial dashboard is worse than an empty one.</p>
 */
@Composable
private fun PositionPanel(cashMinor: Long?, floatMinor: Long?) {
    // Two columns share the width comfortably at ordinary text sizes. Past roughly 130% they
    // do not: each figure gets half a screen and a full amount wraps mid-number. Stacking
    // gives each the whole width, which is the difference between a readable figure and one
    // an agent has to reassemble.
    val stacked = LocalDensity.current.fontScale > 1.3f

    ZaziPanel(
        color = MaterialTheme.colorScheme.primaryContainer,
        contentColor = MaterialTheme.colorScheme.onPrimaryContainer
    ) {
        if (stacked) {
            Column(Modifier.fillMaxWidth().padding(Spacing.large)) {
                PositionFigure("Net cash today", cashMinor)
                Spacer(Modifier.height(Spacing.medium))
                HorizontalDivider(
                    color = MaterialTheme.colorScheme.onPrimaryContainer.copy(alpha = 0.22f)
                )
                Spacer(Modifier.height(Spacing.medium))
                PositionFigure("Net float today", floatMinor)
            }
        } else {
            Row(Modifier.fillMaxWidth().padding(Spacing.large)) {
                PositionFigure("Net cash today", cashMinor, Modifier.weight(1f))
                // A hairline rather than a gap: the two are read together and move in
                // opposite directions, so they should look like one statement, not two panels.
                VerticalDivider(
                    modifier = Modifier.heightIn(min = Sizing.secondaryAction),
                    color = MaterialTheme.colorScheme.onPrimaryContainer.copy(alpha = 0.22f)
                )
                PositionFigure("Net float today", floatMinor, Modifier.weight(1f).padding(start = Spacing.medium))
            }
        }
    }
}

@Composable
private fun PositionFigure(label: String, minor: Long?, modifier: Modifier = Modifier) {
    Column(modifier = modifier) {
        Text(label, style = MaterialTheme.typography.labelMedium)
        Spacer(Modifier.height(Spacing.tight))
        Text(
            // These are today's net movement, not balances — the panel sums the day's stored
            // deltas. Labelled "Cash" with a bare "−₵50.00", an agent read it as holding
            // negative cash. So the label says "net … today", and a rise carries its "+" as a
            // fall carries its "−", which is how a change reads rather than an amount held.
            minor?.let { (if (it > 0) "+" else "") + MoneyFormat.format(it) } ?: "—",
            style = MaterialTheme.typography.headlineSmall,
            fontWeight = FontWeight.SemiBold
            // Deliberately not capped to one line. At a large font scale on a narrow screen
            // a capped figure ellipsises, and a truncated balance is not untidy — it is a
            // different number. Wrapping is the honest failure.
        )
    }
}

/**
 * What has and has not reached the server.
 *
 * <p>One settled line in the ordinary case. The counts only matter when they are not zero,
 * and four rows reading "0" all day taught an agent to stop looking at this panel.</p>
 */
@Composable
private fun DeliverySummary(state: DashboardUiState, onSyncNow: () -> Unit) {
    ZaziPanel {
        Column(Modifier.padding(Spacing.large)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(
                        when {
                            state.unsyncedCount > 0 -> "${state.unsyncedCount} waiting to sync"
                            state.syncedTodayCount > 0 -> "All synced"
                            else -> "Nothing recorded yet"
                        },
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = FontWeight.SemiBold
                    )
                    Spacer(Modifier.height(Spacing.hairline))
                    Text(
                        when {
                            state.unsyncedCount > 0 && !state.isOnline ->
                                "Saved on this device. They will send when you are back online."
                            state.unsyncedCount > 0 -> "Sending automatically."
                            state.syncedTodayCount > 0 -> "${state.syncedTodayCount} sent today."
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

            // Never auto-resolved and never hidden. The one thing on this screen that needs a
            // person rather than time, so it is the one thing given a filled surface.
            if (state.needsAttentionCount > 0) {
                Spacer(Modifier.height(Spacing.small))
                StatusLabel(
                    text = "${state.needsAttentionCount} need review",
                    tone = StatusTone.Attention
                )
            }
        }
    }
}

/** The agent's own record of what this device captured. */
@Composable
private fun ActivitySection(
    items: List<ActivityItem>,
    filter: ActivityFilter,
    onFilterChanged: (ActivityFilter) -> Unit,
    onSelect: (ActivityItem) -> Unit
) {
    SectionHeader("Activity")

    Spacer(Modifier.height(Spacing.small))

    SegmentedFilter(
        options = ActivityFilter.entries.map { it.label },
        selectedIndex = ActivityFilter.entries.indexOf(filter),
        onSelect = { index -> onFilterChanged(ActivityFilter.entries[index]) }
    )

    Spacer(Modifier.height(Spacing.medium))

    if (items.isEmpty()) {
        EmptyState(
            title = when (filter) {
                ActivityFilter.TODAY -> "No transactions today"
                ActivityFilter.YESTERDAY -> "No transactions yesterday"
                ActivityFilter.LAST_SEVEN_DAYS -> "No transactions this week"
            },
            detail = "Everything you record on this device appears here, newest first."
        )
        return
    }

    // A Column, not a LazyColumn: this sits inside a scrolling parent, where nesting a lazy
    // list of the same orientation is a measurement error rather than an optimisation. The
    // query is capped well below any size where laziness would pay.
    ZaziPanel {
        Column {
            items.forEachIndexed { index, item ->
                if (index > 0) {
                    HorizontalDivider(
                        // Inset past the badge, so the rule separates the text rather than
                        // cutting the row in half.
                        modifier = Modifier.padding(start = 68.dp),
                        color = MaterialTheme.colorScheme.outlineVariant
                    )
                }
                ActivityRow(item, onClick = { onSelect(item) })
            }
        }
    }
}

@Composable
private fun ActivityRow(item: ActivityItem, onClick: () -> Unit) {
    // Direction comes from the stored cash delta, never re-derived from the type. Direction
    // was decided once at capture by LedgerProjection; deciding it again here would be a
    // second opinion that could disagree with the figures above.
    val incoming = item.cashDeltaMinor >= 0

    Row(
        // Clickable before padding, so the whole row is the target rather than the text
        // inside it — this is tapped on a phone held in one hand at a counter.
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .heightIn(min = Sizing.minimumTouchTarget)
            .padding(horizontal = Spacing.medium, vertical = Spacing.small),
        verticalAlignment = Alignment.CenterVertically
    ) {
        DirectionBadge(incoming)
        Spacer(Modifier.width(Spacing.small))

        Column(Modifier.weight(1f)) {
            Text(
                item.label,
                style = MaterialTheme.typography.bodyLarge,
                fontWeight = FontWeight.Medium,
                maxLines = 1
            )
            Spacer(Modifier.height(Spacing.hairline))
            Text(
                buildString {
                    append(item.provider)
                    append(" · ")
                    append(formatClock(item.atUtcMillis))
                    // Worth saying: an agent who did not type this needs to know where it
                    // came from before they trust it.
                    if (item.capturedAutomatically) append(" · from SMS")
                },
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1
            )
        }

        Spacer(Modifier.width(Spacing.snug))

        Column(horizontalAlignment = Alignment.End) {
            Text(
                (if (incoming) "+" else "−") + MoneyFormat.format(item.amountMinor),
                style = MaterialTheme.typography.bodyLarge,
                fontWeight = FontWeight.SemiBold,
                // Same reason as the balance: an amount may wrap, never truncate.
                // Money in is tinted; money out stays ordinary ink. The sign is still there,
                // so colour adds emphasis rather than carrying the meaning.
                color = if (incoming) {
                    MaterialTheme.colorScheme.tertiary
                } else {
                    MaterialTheme.colorScheme.onSurface
                },
                maxLines = 1
            )
            Spacer(Modifier.height(Spacing.hairline))
            DeliveryLabel(item.delivery)
        }
    }
}

/** Delivery state, said the same way in the list and on the record. */
@Composable
private fun DeliveryLabel(delivery: ActivityDelivery) {
    StatusLabel(
        text = delivery.label,
        tone = when (delivery) {
            ActivityDelivery.SENT -> StatusTone.Neutral
            ActivityDelivery.SENDING -> StatusTone.Progress
            ActivityDelivery.NEEDS_REVIEW -> StatusTone.Attention
        }
    )
}

/** Local wall-clock time. Ghana observes UTC+0 year-round, so this is also the business day. */
private fun formatClock(utcMillis: Long): String =
    DateTimeFormatter.ofPattern("HH:mm")
        .format(Instant.ofEpochMilli(utcMillis).atZone(ZoneId.systemDefault()))

@Composable
private fun CaptureModeRow(title: String, detail: String) {
    Column {
        Text(title, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium)
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
 * <p>One panel, one action. It was four paragraphs that dominated the screen and buried the
 * choice; the reassurance that declining is fine belongs beside the button, not below it.</p>
 */
@Composable
private fun SmsPermissionPanel(onRequest: () -> Unit) {
    ZaziPanel(border = true, color = MaterialTheme.colorScheme.surface) {
        Column(Modifier.padding(Spacing.large)) {
            Text(
                "Record transactions automatically",
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.SemiBold
            )
            Spacer(Modifier.height(Spacing.tight))
            Text(
                "Zazi can read incoming mobile-money alerts so you do not have to type them. " +
                    "It never reads your other messages and never sends any.",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Spacer(Modifier.height(Spacing.medium))
            // A filled button, not a text one. Beside another line of prose a bare text
            // button reads as more prose, and this is the only thing on the panel to press.
            FilledTonalButton(
                onClick = onRequest,
                shape = Radius.control,
                modifier = Modifier.heightIn(min = Sizing.secondaryAction)
            ) {
                Text("Allow access")
            }
            Spacer(Modifier.height(Spacing.snug))
            Text(
                "Recording by hand keeps working.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

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
                            .heightIn(min = Sizing.primaryAction)
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
                modifier = Modifier.fillMaxWidth().heightIn(min = Sizing.secondaryAction)
            ) {
                Text("Done")
            }
        }
    }
}

/**
 * One transaction, in full.
 *
 * <p>Exists so an agent can answer a customer standing in front of them — what was the
 * reference, which number was it, has it actually gone — without phoning anyone. It is also
 * the only place a stopped transaction can be acted on.</p>
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TransactionDetailScreen(
    detail: TransactionDetail?,
    isRetrying: Boolean,
    onRetry: () -> Unit,
    onBack: () -> Unit,
    reporting: ParsingReportUiState = ParsingReportUiState(),
    onReport: (ParsingVerdict, String) -> Unit = { _, _ -> },
    onReportDismissed: () -> Unit = {}
) {
    var reportOpen by rememberSaveable(detail?.clientTransactionId) { mutableStateOf(false) }
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Transaction") },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        // AutoMirrored: the manifest declares supportsRtl, and a back arrow that does
                        // not flip points the wrong way in a right-to-left layout.
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                    }
                }
            )
        }
    ) { insets ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(insets)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 20.dp)
        ) {
            if (detail == null) {
                // Reached by tapping a row, so this is a race rather than a wrong link: the
                // transaction was there a moment ago. Said plainly instead of an empty screen.
                Spacer(Modifier.height(16.dp))
                Text(
                    "This transaction is no longer on this device.",
                    style = MaterialTheme.typography.bodyLarge
                )
                return@Column
            }

            val incoming = detail.cashDeltaMinor >= 0

            Spacer(Modifier.height(8.dp))
            Text(
                (if (incoming) "+" else "−") + MoneyFormat.format(detail.amountMinor),
                style = MaterialTheme.typography.displaySmall
            )
            Text(
                "${detail.label} · ${detail.provider}",
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            Spacer(Modifier.height(20.dp))

            Card(modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(4.dp)) {
                    DetailRow("When", formatStamp(detail.atUtcMillis))
                    DetailRow("Recorded", if (detail.capturedAutomatically) "Automatically, from SMS" else "By hand")
                    DetailRow("Customer", detail.customerPhone ?: "Not recorded")
                    DetailRow("Provider reference", detail.reference ?: "Not recorded")
                    // The handle to quote to support. Shown last because it is the least
                    // meaningful to the agent and the most useful to whoever they call.
                    DetailRow("Zazi reference", detail.shortReference)
                }
            }

            Spacer(Modifier.height(16.dp))

            Text("Delivery", style = MaterialTheme.typography.titleMedium)
            Spacer(Modifier.height(8.dp))

            Card(modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp)) {
                    DeliveryLabel(detail.delivery)

                    detail.guidance?.let { guidance ->
                        Spacer(Modifier.height(8.dp))
                        Text(guidance, style = MaterialTheme.typography.bodyMedium)
                    }

                    // The reason code is the server's word, not a message written for an
                    // agent, so it is labelled as a diagnostic rather than presented as an
                    // explanation they are expected to understand.
                    detail.lastReasonCode?.let { reason ->
                        Spacer(Modifier.height(8.dp))
                        Text(
                            "Reported: $reason",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }

                    if (detail.isRetryable) {
                        Spacer(Modifier.height(16.dp))
                        Button(
                            onClick = onRetry,
                            enabled = !isRetrying,
                            modifier = Modifier.fillMaxWidth().heightIn(min = Sizing.secondaryAction)
                        ) {
                            if (isRetrying) {
                                CircularProgressIndicator(
                                    modifier = Modifier.size(20.dp),
                                    color = MaterialTheme.colorScheme.onPrimary,
                                    strokeWidth = 2.dp
                                )
                            } else {
                                Text("Try again")
                            }
                        }
                    }
                }
            }

            // Offered only where it can do something. A transaction typed in by hand has no
            // provider message behind it, and one whose message the retention purge has
            // cleared has nothing left to send — in both cases the button would be a promise
            // the app cannot keep.
            if (reporting.canReport) {
                Spacer(Modifier.height(16.dp))

                Text("Is this right?", style = MaterialTheme.typography.titleMedium)
                Spacer(Modifier.height(4.dp))
                Text(
                    "You know what happened at the counter. If Zazi read this message wrongly, "
                        + "telling us sends the message itself so it can be fixed.",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )

                Spacer(Modifier.height(12.dp))

                when {
                    reporting.sent -> Text(
                        "Thank you — we have this message.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.tertiary
                    )

                    else -> OutlinedButton(
                        onClick = { reportOpen = true },
                        enabled = !reporting.isSending,
                        modifier = Modifier.fillMaxWidth().heightIn(min = Sizing.secondaryAction)
                    ) {
                        Text("This is wrong")
                    }
                }

                reporting.failure?.let { failure ->
                    Spacer(Modifier.height(8.dp))
                    Text(
                        failure,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error
                    )
                }
            }

            Spacer(Modifier.height(24.dp))
        }
    }

    if (reportOpen && detail != null) {
        ReportParsingDialog(
            isSending = reporting.isSending,
            onDismiss = {
                reportOpen = false
                onReportDismissed()
            },
            onSubmit = { verdict, note ->
                reportOpen = false
                onReport(verdict, note)
            }
        )
    }
}

/**
 * What an agent is told before a provider's message leaves their handset.
 *
 * <p>Message bodies do not sync. This dialog is the moment that rule is set aside, so it says
 * plainly what will be sent and asks for a deliberate tap rather than burying consent in a
 * setting somebody agreed to once.</p>
 */
@Composable
private fun ReportParsingDialog(
    isSending: Boolean,
    onDismiss: () -> Unit,
    onSubmit: (ParsingVerdict, String) -> Unit
) {
    var verdict by rememberSaveable { mutableStateOf(ParsingVerdict.WRONG_DIRECTION) }
    var note by rememberSaveable { mutableStateOf("") }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("What did Zazi get wrong?") },
        text = {
            Column {
                ParsingVerdict.entries.forEach { option ->
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clickable { verdict = option }
                            .heightIn(min = Sizing.minimumTouchTarget),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        RadioButton(selected = verdict == option, onClick = { verdict = option })
                        Spacer(Modifier.width(Spacing.snug))
                        Text(option.label, style = MaterialTheme.typography.bodyLarge)
                    }
                }

                Spacer(Modifier.height(Spacing.small))

                OutlinedTextField(
                    value = note,
                    onValueChange = { note = it.take(200) },
                    label = { Text("Anything to add (optional)") },
                    singleLine = false,
                    modifier = Modifier.fillMaxWidth()
                )

                Spacer(Modifier.height(Spacing.small))

                // Said before the tap, not after. An agent agreeing to send one message should
                // know that is what they are agreeing to.
                Text(
                    "This sends the provider's message for this transaction, and nothing else. "
                        + "No other messages are sent.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        },
        confirmButton = {
            TextButton(onClick = { onSubmit(verdict, note) }, enabled = !isSending) {
                Text("Send message")
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text("Cancel") }
        }
    )
}

@Composable
private fun DetailRow(label: String, value: String) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 10.dp),
        verticalAlignment = Alignment.Top
    ) {
        Text(
            label,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.width(150.dp)
        )
        Text(value, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
    }
}

/** Full local timestamp, for checking against a provider's own record. */
private fun formatStamp(utcMillis: Long): String =
    DateTimeFormatter.ofPattern("d MMM yyyy, HH:mm")
        .format(Instant.ofEpochMilli(utcMillis).atZone(ZoneId.systemDefault()))

/**
 * Shown when the server no longer trusts this device.
 *
 * <p>The way back is a new activation code, not a sign-in. A worker activated by code has no
 * account to sign in with, and telling them to sign in would send them looking for
 * credentials that do not exist. Only the owner can restore access, and the copy says so.</p>
 */
@Composable
fun DeviceRevokedScreen(queuedWorkCount: Int, onStartOver: () -> Unit) {
    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.Center
    ) {
        Text("Device access revoked", style = MaterialTheme.typography.headlineSmall)
        Spacer(Modifier.height(12.dp))

        Text(
            "This device is no longer registered. Ask your business owner to authorise it " +
                "again and give you a new activation code.",
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
        Button(onClick = onStartOver, modifier = Modifier.fillMaxWidth().heightIn(min = Sizing.secondaryAction)) {
            Text("Enter a new activation code")
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

/**
 * Shown once when local data could not be decrypted and had to be set aside.
 *
 * <p>Deliberately blunt about the consequence. What was lost is transactions this agent
 * captured and had not yet synced, and the honest thing is to say so rather than let them
 * discover a gap in their own records later and doubt themselves.</p>
 */
@Composable
fun DataLostNotice(onDismiss: () -> Unit) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(Spacing.medium),
        verticalArrangement = Arrangement.Center
    ) {
        Text(
            "Saved records could not be opened",
            style = MaterialTheme.typography.headlineSmall,
            color = MaterialTheme.colorScheme.onBackground
        )

        Spacer(Modifier.height(Spacing.small))

        Text(
            "This phone could not unlock the records stored on it. This can happen after a " +
                "phone is restored from a backup or its security keys are reset.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )

        Spacer(Modifier.height(Spacing.small))

        Text(
            // The part that matters to them, stated without hedging.
            "Any transactions that had not yet been sent may be lost. Transactions already " +
                "sent are safe on the server. Please check your records for today and " +
                "re-enter anything missing.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onBackground
        )

        Spacer(Modifier.height(Spacing.large))

        ZaziPrimaryButton(text = "Continue", onClick = onDismiss)
    }
}
