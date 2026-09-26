package app.zazi.ui

import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * When the capture form may be opened.
 *
 * <p>Two rules meet here and they pulled in opposite directions. Automatic capture must not sit
 * beside a form for typing transactions in by hand: two records of one payment cannot be told
 * apart afterwards. But a message the phone received and could not read has to be finishable,
 * and the form is the only way that money ever reaches the books.</p>
 *
 * <p>Getting that wrong cost a client their first day of testing. The route refused every
 * opening while capture was on, so "Record it" on a held message navigated and bounced
 * straight back — the button did nothing at all, on precisely the phones that need it, while
 * "Not a transaction" worked and quietly threw the money away.</p>
 */
class CaptureRouteTest {

    /** The rule as MainActivity applies it, stated once so a change to it fails here. */
    private fun opens(automaticCapture: Boolean, finishingHeldMessage: Boolean): Boolean =
        !automaticCapture || finishingHeldMessage

    @Test
    fun `a held message can always be finished, capture on or off`() {
        assertThat(opens(automaticCapture = true, finishingHeldMessage = true)).isTrue()
        assertThat(opens(automaticCapture = false, finishingHeldMessage = true)).isTrue()
    }

    @Test
    fun `typing one in by hand is refused while the phone is recording for them`() {
        assertThat(opens(automaticCapture = true, finishingHeldMessage = false)).isFalse()
    }

    @Test
    fun `with capture off the form is the only way in, so it always opens`() {
        assertThat(opens(automaticCapture = false, finishingHeldMessage = false)).isTrue()
    }

    @Test
    fun `the only refusal is inventing one beside automatic capture`() {
        val refused = listOf(true, false).flatMap { auto ->
            listOf(true, false).map { held -> Triple(auto, held, opens(auto, held)) }
        }.filterNot { it.third }

        // Exactly one of the four combinations may be refused. Any other is money an agent
        // received and cannot record.
        assertThat(refused).containsExactly(Triple(true, false, false))
    }
}
