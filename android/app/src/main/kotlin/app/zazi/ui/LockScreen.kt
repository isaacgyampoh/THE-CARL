package app.zazi.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import app.zazi.ui.design.Spacing
import app.zazi.ui.design.ZaziPrimaryButton

/**
 * What stands between a phone left on a counter and the business's records.
 *
 * <p>Deliberately says nothing about the day: no balances, no customer numbers, no agent name.
 * A locked screen that still shows the takings has locked nothing worth locking.</p>
 */
@Composable
fun LockScreen(refused: String?, onUnlock: () -> Unit) {
    Surface(
        modifier = Modifier.fillMaxSize(),
        color = MaterialTheme.colorScheme.primaryContainer
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(Spacing.large),
            verticalArrangement = Arrangement.Center,
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Text(
                "Zazi is locked",
                style = MaterialTheme.typography.headlineSmall,
                fontWeight = FontWeight.Bold,
                color = MaterialTheme.colorScheme.onPrimaryContainer
            )
            Spacer(Modifier.height(Spacing.small))
            Text(
                "Unlock with the same PIN or fingerprint you use for this phone.",
                style = MaterialTheme.typography.bodyMedium,
                textAlign = TextAlign.Center,
                color = MaterialTheme.colorScheme.onPrimaryContainer
            )

            // Only a real refusal is shown. Saying "failed" to somebody who pressed cancel
            // reads as the app being broken.
            if (!refused.isNullOrBlank()) {
                Spacer(Modifier.height(Spacing.small))
                Text(
                    refused,
                    style = MaterialTheme.typography.bodySmall,
                    textAlign = TextAlign.Center,
                    color = MaterialTheme.colorScheme.onPrimaryContainer
                )
            }

            Spacer(Modifier.height(Spacing.section))
            ZaziPrimaryButton(text = "Unlock", onClick = onUnlock)
        }
    }
}
