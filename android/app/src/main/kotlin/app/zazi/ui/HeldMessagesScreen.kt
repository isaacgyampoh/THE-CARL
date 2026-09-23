package app.zazi.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import app.zazi.ui.design.Spacing
import app.zazi.ui.design.ZaziPanel
import app.zazi.ui.design.ZaziPrimaryButton
import app.zazi.ui.state.HeldMessageUiItem
import app.zazi.ui.state.MoneyFormat

/**
 * Mobile money messages that arrived and could not be read into a transaction.
 *
 * <p>These used to be stored and shown nowhere. An agent took a deposit, the confirmation
 * arrived, the app kept it as evidence it could not classify, and the money appeared in none
 * of their figures with nothing on any screen to say why. At the counter that is
 * indistinguishable from the app being broken.</p>
 *
 * <p>So the message itself is shown — the agent can read what the app could not — with the two
 * answers they actually have: record it, or say it was not a transaction.</p>
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HeldMessagesScreen(
    items: List<HeldMessageUiItem>,
    onRecord: (HeldMessageUiItem) -> Unit,
    onDismiss: (HeldMessageUiItem) -> Unit,
    onBack: () -> Unit
) {
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Could not be read") },
                navigationIcon = {
                    IconButton(onClick = onBack) {
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
                .padding(horizontal = Spacing.large)
        ) {
            if (items.isEmpty()) {
                Spacer(Modifier.height(Spacing.section))
                Text(
                    "Nothing is waiting. Every message that arrived has been recorded.",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                return@Column
            }

            Spacer(Modifier.height(Spacing.small))
            Text(
                "These arrived on this phone but Zazi could not tell what they were, so they " +
                    "are in none of your figures. Read each one and record it, or say it was " +
                    "not a transaction.",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            items.forEach { item ->
                Spacer(Modifier.height(Spacing.medium))
                HeldMessageCard(item, onRecord = { onRecord(item) }, onDismiss = { onDismiss(item) })
            }

            Spacer(Modifier.height(Spacing.large))
        }
    }
}

@Composable
private fun HeldMessageCard(
    item: HeldMessageUiItem,
    onRecord: () -> Unit,
    onDismiss: () -> Unit
) {
    var confirming by rememberSaveable(item.evidenceId) { mutableStateOf(false) }

    ZaziPanel {
        Column(Modifier.padding(Spacing.medium)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(
                        item.providerLabel,
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = FontWeight.SemiBold
                    )
                    Text(
                        item.arrivedAtLabel,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
                // Shown only when the parser actually recovered one. A blank here is the
                // honest answer; a zero would read as a transaction of nothing.
                item.amountMinor?.let {
                    Text(
                        MoneyFormat.format(it),
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = FontWeight.SemiBold
                    )
                }
            }

            item.rawMessage?.let { message ->
                Spacer(Modifier.height(Spacing.small))
                // The message as it arrived. The agent is being asked to do what the app
                // could not, so they need the thing the app was looking at.
                Text(message, style = MaterialTheme.typography.bodyMedium)
            }

            item.reason?.let { reason ->
                Spacer(Modifier.height(Spacing.tight))
                Text(
                    reason,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }

            Spacer(Modifier.height(Spacing.small))
            ZaziPrimaryButton(text = "Record it", onClick = onRecord)
            Spacer(Modifier.height(Spacing.hairline))
            // Deliberately the quieter of the two, and confirmed. Dismissing discards the only
            // record that this money ever arrived and cannot be undone from here, so it must
            // not be reachable by one stray tap on a phone being scrolled with a thumb.
            TextButton(onClick = { confirming = true }, modifier = Modifier.fillMaxWidth()) {
                Text("Not a transaction")
            }

            if (confirming) {
                AlertDialog(
                    onDismissRequest = { confirming = false },
                    title = { Text("Remove this message?") },
                    text = {
                        Text(
                            "It will not appear again. If money did arrive, it will stay out " +
                                "of your figures until you record it by hand."
                        )
                    },
                    confirmButton = {
                        TextButton(onClick = {
                            confirming = false
                            onDismiss()
                        }) { Text("Remove") }
                    },
                    dismissButton = {
                        TextButton(onClick = { confirming = false }) { Text("Keep it") }
                    }
                )
            }
        }
    }
}
