package app.zazi.core.domain.parser

import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * What leaves an agent's phone when a wording is sent to be fixed.
 *
 * <p>The shape of a message is what fixes a parser — where the amount sits, how the reference
 * is labelled, which words state the direction. Who was in it is not, and a customer's name
 * and number are the two things Zazi would be least forgiven for sending anywhere.</p>
 */
class MaskingTest {

    @Test
    fun `a customer's number never leaves the phone`() {
        val masked = Masking.maskPeople(
            "Cash In of GHS 500.00 from 0241000001. Ref: MP240815.1201.A00001"
        )

        assertThat(masked).doesNotContain("0241000001")
        assertThat(masked).contains("[number]")
    }

    @Test
    fun `a customer's name never leaves the phone`() {
        val masked = Masking.maskPeople(
            "Payment received for GHS 295.00 from AARON AMPEM LARTEY. Balance GHS 1337.16"
        )

        assertThat(masked).doesNotContain("AARON")
        assertThat(masked).doesNotContain("LARTEY")
    }

    @Test
    fun `the shape of the message survives, because that is the whole point`() {
        val masked = Masking.maskPeople(
            "Cash In of GHS 1,250.00 from 0241000001 KWESI ANTWI. Ref: MP240815.1201.A00001. " +
                "Your MoMo agent balance is GHS 12,340.00"
        )

        // The words that state the direction, the amount with its punctuation, and the
        // reference the network keys its own record on.
        assertThat(masked).contains("Cash In")
        assertThat(masked).contains("GHS 1,250.00")
        assertThat(masked).contains("MP240815.1201.A00001")
        assertThat(masked).contains("GHS 12,340.00")
    }

    @Test
    fun `a number written the international way is masked too`() {
        val masked = Masking.maskPeople("Deposit of GHS 40.00 from 233241000001. Ref: R1")

        assertThat(masked).doesNotContain("233241000001")
    }

    @Test
    fun `a transaction id is not mistaken for a phone number`() {
        // MTN's ids are long runs of digits and a number pattern that matched inside them
        // would destroy the one field a dispute is settled with.
        val masked = Masking.maskPeople(
            "Deposit of GHS 50.00. Transaction ID: 90079732268. Balance GHS 100.00"
        )

        assertThat(masked).contains("90079732268")
    }
}
