package app.zazi.ui

import app.zazi.ui.state.ActivityDelivery
import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * How outbox state is presented to the agent.
 *
 * <p>The mapping collapses six states into three, and one of those decisions is easy to get
 * backwards later, which is why it is pinned here rather than left to the reader of the
 * <c>when</c>.</p>
 */
class ActivityDeliveryTest {

    @Test
    fun `a pruned outbox row means delivered, not missing`() {
        // The outbox row is deleted once an item is settled. Treating the absence as unknown
        // would show a delivered transaction as though something were wrong with it — on the
        // agent's own record of work they have already done.
        assertThat(ActivityDelivery.fromOutboxState(null)).isEqualTo(ActivityDelivery.SENT)
    }

    @Test
    fun `everything still moving reads as sending`() {
        // Pending, syncing and retrying are three different facts to the sync engine and the
        // same one to the person holding the phone: it is on its way, nothing is required of
        // you. Retrying in particular must not look like a failure.
        for (state in listOf("PENDING", "SYNCING", "RETRYABLE_FAILURE")) {
            assertThat(ActivityDelivery.fromOutboxState(state))
                .isEqualTo(ActivityDelivery.SENDING)
        }
    }

    @Test
    fun `only the states that need a person are separated out`() {
        // These never resolve by waiting, so they are the one thing in the list worth
        // interrupting someone for.
        for (state in listOf("CONFLICT", "DEAD_LETTER")) {
            assertThat(ActivityDelivery.fromOutboxState(state))
                .isEqualTo(ActivityDelivery.NEEDS_REVIEW)
        }
    }

    @Test
    fun `an unrecognised state is treated as still moving, never as delivered`() {
        // A state this build has not heard of is most likely a newer sync state. Guessing
        // "sent" would tell an agent their transaction reached the server when nothing knows
        // that; guessing "sending" is wrong in a direction that cannot mislead.
        assertThat(ActivityDelivery.fromOutboxState("SOMETHING_NEW"))
            .isEqualTo(ActivityDelivery.SENDING)
    }
}
