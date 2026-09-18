package app.zazi.ui.design

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp

/**
 * The pieces every Zazi screen is built from.
 *
 * <p>These exist so a change of mind happens once. Before them each screen re-stated its own
 * button height, its own panel padding and its own idea of what a status looks like, which is
 * how interfaces drift into looking assembled rather than designed.</p>
 */

/** The single most important action on a screen. One per screen, at most. */
@Composable
fun ZaziPrimaryButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    busy: Boolean = false
) {
    Button(
        onClick = onClick,
        enabled = enabled && !busy,
        shape = Radius.control,
        // heightIn, not height: at a large font scale a fixed height clips its own label.
        modifier = modifier.fillMaxWidth().heightIn(min = Sizing.primaryAction)
    ) {
        if (busy) {
            CircularProgressIndicator(
                modifier = Modifier.size(20.dp),
                color = MaterialTheme.colorScheme.onPrimary,
                strokeWidth = 2.dp
            )
        } else {
            Text(text, style = MaterialTheme.typography.titleMedium)
        }
    }
}

/**
 * A panel.
 *
 * <p>Deliberately flat. Elevation is reserved for things that genuinely float — the anchored
 * action bar — so that stacking cards inside cards cannot happen by accident. Separation here
 * comes from a surface change and a hairline, which is quieter and scans better in a list.</p>
 */
@Composable
fun ZaziPanel(
    modifier: Modifier = Modifier,
    color: Color = MaterialTheme.colorScheme.surfaceContainer,
    contentColor: Color = MaterialTheme.colorScheme.onSurface,
    border: Boolean = false,
    content: @Composable () -> Unit
) {
    Surface(
        modifier = modifier.fillMaxWidth(),
        color = color,
        contentColor = contentColor,
        shape = Radius.panel,
        border = if (border) BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant) else null
    ) {
        content()
    }
}

/** A section title, with an optional trailing action. */
@Composable
fun SectionHeader(
    title: String,
    modifier: Modifier = Modifier,
    trailing: (@Composable () -> Unit)? = null
) {
    Row(
        modifier = modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            title,
            style = MaterialTheme.typography.titleMedium,
            fontWeight = FontWeight.SemiBold,
            modifier = Modifier.weight(1f)
        )
        trailing?.invoke()
    }
}

/**
 * Cash in or cash out, before any figure is read.
 *
 * <p>An agent scanning a shift reads this column, not the amounts. The arrow differs as well
 * as the tint and the amount keeps its sign, so the direction survives a monochrome screen,
 * a colour-blind reader and a phone in direct sun.</p>
 */
@Composable
fun DirectionBadge(incoming: Boolean, modifier: Modifier = Modifier) {
    val colours = MaterialTheme.colorScheme

    Box(
        modifier = modifier
            .size(Sizing.badge)
            .background(
                if (incoming) colours.tertiaryContainer else colours.surfaceContainerHighest,
                CircleShape
            ),
        contentAlignment = Alignment.Center
    ) {
        Icon(
            imageVector = if (incoming) Icons.Filled.KeyboardArrowDown else Icons.Filled.KeyboardArrowUp,
            // Described, because the row's text says the direction only via the sign.
            contentDescription = if (incoming) "Cash in" else "Cash out",
            tint = if (incoming) colours.onTertiaryContainer else colours.onSurfaceVariant,
            modifier = Modifier.size(24.dp)
        )
    }
}

/** How delivery state is shown wherever it appears. */
enum class StatusTone { Neutral, Progress, Attention }

/**
 * A state, said once and the same way everywhere.
 *
 * <p>Shape carries the meaning as much as colour: attention states get a filled surface,
 * settled ones stay quiet text. A status that only differed by hue would vanish for a
 * colour-blind reader and in bright sunlight.</p>
 */
@Composable
fun StatusLabel(text: String, tone: StatusTone, modifier: Modifier = Modifier) {
    val colours = MaterialTheme.colorScheme

    when (tone) {
        StatusTone.Attention -> Surface(
            modifier = modifier,
            color = colours.errorContainer,
            contentColor = colours.onErrorContainer,
            shape = Radius.small
        ) {
            Text(
                text,
                style = MaterialTheme.typography.labelMedium,
                fontWeight = FontWeight.Medium,
                modifier = Modifier.padding(horizontal = Spacing.snug, vertical = Spacing.tight)
            )
        }

        StatusTone.Progress -> Text(
            text,
            style = MaterialTheme.typography.labelMedium,
            color = colours.primary,
            fontWeight = FontWeight.Medium,
            modifier = modifier
        )

        StatusTone.Neutral -> Text(
            text,
            style = MaterialTheme.typography.labelMedium,
            color = colours.onSurfaceVariant,
            modifier = modifier
        )
    }
}

/**
 * The filter control.
 *
 * <p>A segmented strip rather than loose Material chips: the options are one choice between
 * mutually exclusive periods, and a strip says that where a row of pills does not. Selection
 * is carried by fill, weight and a check — three signals, none of them only colour.</p>
 */
@Composable
fun SegmentedFilter(
    options: List<String>,
    selectedIndex: Int,
    onSelect: (Int) -> Unit,
    modifier: Modifier = Modifier
) {
    val colours = MaterialTheme.colorScheme

    Surface(
        modifier = modifier.fillMaxWidth(),
        color = colours.surfaceContainerHighest,
        shape = Radius.control
    ) {
        Row(Modifier.padding(Spacing.tight), horizontalArrangement = Arrangement.spacedBy(Spacing.tight)) {
            options.forEachIndexed { index, label ->
                val selected = index == selectedIndex

                Surface(
                    modifier = Modifier
                        .weight(1f)
                        // selectable, not clickable: this announces as a radio-style choice
                        // and reports which one is chosen.
                        .selectable(
                            selected = selected,
                            role = Role.RadioButton,
                            onClick = { onSelect(index) }
                        )
                        .heightIn(min = Sizing.minimumTouchTarget - Spacing.snug),
                    color = if (selected) colours.surface else Color.Transparent,
                    contentColor = if (selected) colours.onSurface else colours.onSurfaceVariant,
                    shape = Radius.small
                ) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.Center,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        if (selected) {
                            Icon(
                                Icons.Filled.Check,
                                contentDescription = null,
                                modifier = Modifier.size(14.dp),
                                tint = colours.primary
                            )
                            Spacer(Modifier.width(Spacing.tight))
                        }
                        Text(
                            label,
                            style = MaterialTheme.typography.labelLarge,
                            fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
                            textAlign = TextAlign.Center
                            // Not capped to one line: at a large font scale "Last 7 days"
                            // truncated to "Last 7", which is a different period. Wrapping
                            // keeps the option honest about what it selects.
                        )
                    }
                }
            }
        }
    }
}

/**
 * An empty list, explained.
 *
 * <p>States the actual situation rather than performing cheerfulness at someone whose day has
 * simply been quiet, and carries no illustration — there is nothing to illustrate.</p>
 */
@Composable
fun EmptyState(title: String, detail: String, modifier: Modifier = Modifier) {
    Column(modifier = modifier.fillMaxWidth().padding(vertical = Spacing.large)) {
        Text(title, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
        Spacer(Modifier.height(Spacing.tight))
        Text(
            detail,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

/**
 * Something went wrong, said in the agent's terms.
 *
 * <p>One presentation for failure across the app. Never carries a stack trace, a status code
 * or a database message — those help nobody standing at a counter.</p>
 */
@Composable
fun ErrorNotice(message: String, modifier: Modifier = Modifier) {
    Surface(
        modifier = modifier.fillMaxWidth(),
        color = MaterialTheme.colorScheme.errorContainer,
        contentColor = MaterialTheme.colorScheme.onErrorContainer,
        shape = Radius.small
    ) {
        Text(
            message,
            style = MaterialTheme.typography.bodyMedium,
            textAlign = TextAlign.Start,
            modifier = Modifier.padding(horizontal = Spacing.small, vertical = Spacing.snug)
        )
    }
}

/** Used by the anchored action bar, which is the one thing that genuinely floats. */
@Composable
fun AnchoredActionBar(content: @Composable () -> Unit) {
    Surface(tonalElevation = 3.dp, color = MaterialTheme.colorScheme.surface) {
        Box(Modifier.padding(horizontal = Spacing.large, vertical = Spacing.small)) { content() }
    }
}
