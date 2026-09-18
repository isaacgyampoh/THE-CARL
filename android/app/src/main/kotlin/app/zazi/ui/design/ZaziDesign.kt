package app.zazi.ui.design

import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.unit.dp

/**
 * The measurements the whole application is built from.
 *
 * <p>Before this existed the screens used 2, 4, 8, 10, 12, 16, 20 and 24dp more or less
 * interchangeably, and buttons were 52dp in one place and 56dp in another. None of it was
 * visible as a bug, and all of it was visible as slight untidiness — the difference between
 * software that looks made and software that looks assembled.</p>
 *
 * <p>A 4dp scale, used everywhere. Nothing here is decorative; each value exists because
 * something needs it.</p>
 */
object Spacing {
    /** Between a label and the thing it labels. */
    val hairline = 2.dp

    /** Within a single idea — a title and its supporting line. */
    val tight = 4.dp

    /** Between related elements in a row. */
    val snug = 8.dp

    /** The standard gap between controls. */
    val small = 12.dp

    /** Inside a row: the breathing room that makes a list scannable. */
    val medium = 16.dp

    /** Screen gutters, and padding inside a panel. */
    val large = 20.dp

    /** Between sections that are about different things. */
    val section = 28.dp
}

/**
 * Corner radii.
 *
 * <p>Three values, not seven. Panels are softer than controls, and controls are softer than
 * the small status shapes, which reads as deliberate rather than as whatever each component
 * happened to default to.</p>
 */
object Radius {
    val small = RoundedCornerShape(8.dp)
    val control = RoundedCornerShape(14.dp)
    val panel = RoundedCornerShape(20.dp)
}

/** Fixed sizes that recur, so they cannot drift apart. */
object Sizing {
    /**
     * Primary action height.
     *
     * <p>Comfortably above the 48dp minimum touch target, because this is pressed dozens of
     * times a shift by someone holding a phone in one hand at a counter.</p>
     */
    val primaryAction = 56.dp

    /** Secondary actions, still above the accessible minimum. */
    val secondaryAction = 48.dp

    /** The direction badge on a transaction row. */
    val badge = 40.dp

    /** The smallest a tappable thing may be. Android's accessibility floor. */
    val minimumTouchTarget = 48.dp

    val screenGutter = PaddingValues(horizontal = 20.dp)
}
