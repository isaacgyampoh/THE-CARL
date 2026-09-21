package app.zazi.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import app.zazi.core.data.network.DayCloseResponse
import app.zazi.ui.design.AnchoredActionBar
import app.zazi.ui.design.ErrorNotice
import app.zazi.ui.design.Spacing
import app.zazi.ui.design.StatusLabel
import app.zazi.ui.design.StatusTone
import app.zazi.ui.design.ZaziPanel
import app.zazi.ui.design.ZaziPrimaryButton
import app.zazi.ui.state.CloseDay
import app.zazi.ui.state.MoneyFormat

/**
 * End of day: the agent counts the cash in the drawer and the float on every SIM, and is told
 * at once whether it agrees with what they recorded — on this phone and any other.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CloseDayScreen(
    /** Transactions on this phone not yet delivered; the server cannot count what it has not seen. */
    unsentCount: Int,
    isOnline: Boolean,
    busy: Boolean,
    error: String?,
    result: DayCloseResponse?,
    onSubmit: (cashMinor: Long, floatMinor: Long) -> Unit,
    onBack: () -> Unit
) {
    var cash by rememberSaveable { mutableStateOf("") }
    var float by rememberSaveable { mutableStateOf("") }
    val cashFocus = remember { FocusRequester() }
    val floatFocus = remember { FocusRequester() }
    LaunchedEffect(Unit) { runCatching { cashFocus.requestFocus() } }

    val cashMinor = CloseDay.parseMinor(cash)
    val floatMinor = CloseDay.parseMinor(float)
    val ready = cashMinor != null && floatMinor != null && !busy && isOnline

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Close the day") },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                    }
                }
            )
        },
        bottomBar = {
            AnchoredActionBar {
                if (result == null) {
                    ZaziPrimaryButton(
                        text = "Close the day",
                        onClick = { if (cashMinor != null && floatMinor != null) onSubmit(cashMinor, floatMinor) },
                        enabled = ready,
                        busy = busy
                    )
                } else {
                    ZaziPrimaryButton(text = "Done", onClick = onBack)
                }
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
            if (result != null) {
                ResultPanel(result)
                return@Column
            }

            Text(
                "Count the notes in your drawer and add up the float on all your SIMs. " +
                    "Zazi compares them with everything you recorded since your last close.",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            if (!isOnline) {
                Spacer(Modifier.height(Spacing.medium))
                ErrorNotice("You are offline. Closing needs data or Wi-Fi — or text CLOSE cash float to Zazi.")
            } else if (unsentCount > 0) {
                Spacer(Modifier.height(Spacing.medium))
                // Said before the count, not after: an unsent cash out would otherwise show as
                // a shortage the agent does not have.
                ErrorNotice(
                    "$unsentCount transaction${if (unsentCount == 1) " is" else "s are"} still sending. " +
                        "Wait for ${if (unsentCount == 1) "it" else "them"} to send first, or the count will not agree."
                )
            }

            Spacer(Modifier.height(Spacing.section))
            OutlinedTextField(
                value = cash,
                onValueChange = { cash = it },
                label = { Text("Cash counted") },
                prefix = { Text("₵") },
                textStyle = MaterialTheme.typography.headlineSmall,
                singleLine = true,
                isError = cash.isNotBlank() && cashMinor == null,
                enabled = !busy,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal, imeAction = ImeAction.Next),
                keyboardActions = KeyboardActions(onNext = { floatFocus.requestFocus() }),
                modifier = Modifier.fillMaxWidth().focusRequester(cashFocus)
            )

            Spacer(Modifier.height(Spacing.medium))
            OutlinedTextField(
                value = float,
                onValueChange = { float = it },
                label = { Text("Float on all SIMs") },
                prefix = { Text("₵") },
                textStyle = MaterialTheme.typography.headlineSmall,
                singleLine = true,
                isError = float.isNotBlank() && floatMinor == null,
                enabled = !busy,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal, imeAction = ImeAction.Done),
                modifier = Modifier.fillMaxWidth().focusRequester(floatFocus)
            )

            error?.let {
                Spacer(Modifier.height(Spacing.medium))
                ErrorNotice(it)
            }

            Spacer(Modifier.height(Spacing.large))
        }
    }
}

@Composable
private fun ResultPanel(result: DayCloseResponse) {
    Spacer(Modifier.height(Spacing.small))
    Text(
        CloseDay.headline(result),
        style = MaterialTheme.typography.headlineSmall,
        fontWeight = FontWeight.SemiBold
    )
    Spacer(Modifier.height(Spacing.medium))

    ZaziPanel {
        Column(Modifier.padding(Spacing.medium)) {
            ResultLine("Cash", CloseDay.minor(result.countedCash), CloseDay.difference(result.cashDifference))
            Spacer(Modifier.height(Spacing.small))
            ResultLine("Float", CloseDay.minor(result.countedFloat), CloseDay.difference(result.floatDifference))
        }
    }

    Spacer(Modifier.height(Spacing.medium))
    Text(CloseDay.advice(result), style = MaterialTheme.typography.bodyMedium)
    if (!result.isBaseline) {
        Spacer(Modifier.height(Spacing.small))
        Text(
            "${result.transactionCount} transaction${if (result.transactionCount == 1) "" else "s"} since your last close.",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

@Composable
private fun ResultLine(label: String, countedMinor: Long, difference: String?) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(label, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text(MoneyFormat.format(countedMinor), style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
        }
        difference?.let {
            StatusLabel(
                text = it,
                tone = when {
                    it == "OK" -> StatusTone.Neutral
                    it.startsWith("Short") -> StatusTone.Attention
                    else -> StatusTone.Progress
                }
            )
        }
    }
}
