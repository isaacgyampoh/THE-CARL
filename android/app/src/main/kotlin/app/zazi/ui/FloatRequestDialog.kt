package app.zazi.ui

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import app.zazi.core.data.network.FloatRequestInfo
import app.zazi.ui.design.NetworkBadge
import app.zazi.ui.design.Spacing
import app.zazi.ui.design.StatusLabel
import app.zazi.ui.design.StatusTone
import app.zazi.ui.state.CloseDay
import app.zazi.ui.state.MoneyFormat

/**
 * "I am running out of MTN, send me 500" — asked in the app instead of a phone call nobody
 * writes down. The owner is texted at once, and float they give is recorded against the agent
 * in the same step.
 */
@Composable
fun FloatRequestDialog(
    busy: Boolean,
    error: String?,
    sent: FloatRequestInfo?,
    recent: List<FloatRequestInfo>,
    isOnline: Boolean,
    onSend: (network: String, amountMinor: Long) -> Unit,
    onDismiss: () -> Unit
) {
    var network by rememberSaveable { mutableStateOf("MTN") }
    var amount by rememberSaveable { mutableStateOf("") }
    val amountMinor = CloseDay.parseMinor(amount)?.takeIf { it > 0 }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(if (sent != null) "Request sent" else "Ask for float") },
        text = {
            Column {
                if (sent != null) {
                    Text(
                        "Your owner has been told: ${MoneyFormat.format(CloseDay.minor(sent.amount))} " +
                            "${networkName(sent.network)} float. Reference ${sent.code}. When they give it, " +
                            "it is added to your float automatically.",
                        style = MaterialTheme.typography.bodyMedium
                    )
                } else {
                    Row(
                        Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                        horizontalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        listOf("MTN", "TELECEL", "AIRTELTIGO").forEach { option ->
                            FilterChip(
                                selected = network == option,
                                onClick = { network = option },
                                label = { Text(networkName(option)) },
                                leadingIcon = if (network == option) {
                                    { Icon(Icons.Filled.Check, contentDescription = null, modifier = Modifier.size(18.dp)) }
                                } else null
                            )
                        }
                    }
                    Spacer(Modifier.height(Spacing.small))
                    OutlinedTextField(
                        value = amount,
                        onValueChange = { amount = it },
                        label = { Text("Amount") },
                        prefix = { Text("₵") },
                        singleLine = true,
                        isError = amount.isNotBlank() && amountMinor == null,
                        enabled = !busy,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                        modifier = Modifier.fillMaxWidth()
                    )
                    if (!isOnline) {
                        Spacer(Modifier.height(Spacing.snug))
                        Text(
                            "You are offline. Text FLOAT 500 MTN to Zazi from any phone instead.",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.error
                        )
                    }
                    error?.let {
                        Spacer(Modifier.height(Spacing.snug))
                        Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
                    }
                }

                if (recent.isNotEmpty()) {
                    Spacer(Modifier.height(Spacing.medium))
                    Text("Recent requests", style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.SemiBold)
                    recent.take(3).forEach { request ->
                        HorizontalDivider(Modifier.padding(vertical = Spacing.snug), color = MaterialTheme.colorScheme.outlineVariant)
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            NetworkBadge(request.network, Modifier.size(28.dp))
                            Spacer(Modifier.padding(start = Spacing.snug))
                            Text(
                                MoneyFormat.format(CloseDay.minor(request.amount)),
                                style = MaterialTheme.typography.bodyMedium,
                                modifier = Modifier.weight(1f)
                            )
                            StatusLabel(
                                text = when (request.status) { 1 -> "Given"; 2 -> "Declined"; else -> "Waiting" },
                                tone = when (request.status) { 1 -> StatusTone.Neutral; 2 -> StatusTone.Attention; else -> StatusTone.Progress }
                            )
                        }
                    }
                }
            }
        },
        confirmButton = {
            if (sent != null) {
                TextButton(onClick = onDismiss) { Text("Done") }
            } else {
                TextButton(
                    onClick = { amountMinor?.let { onSend(network, it) } },
                    enabled = amountMinor != null && !busy && isOnline
                ) {
                    if (busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp) else Text("Send request")
                }
            }
        },
        dismissButton = {
            if (sent == null) TextButton(onClick = onDismiss) { Text("Cancel") }
        }
    )
}

private fun networkName(code: String) = when (code.uppercase()) {
    "TELECEL" -> "Telecel"
    "AIRTELTIGO" -> "AirtelTigo"
    else -> "MTN"
}
