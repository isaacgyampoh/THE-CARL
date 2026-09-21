package app.zazi.core.domain.model

import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * One spelling for every customer number, so a search for one finds them all.
 *
 * <p>Mirrored by the C# GhanaPhoneNumberTests. The two implementations must agree, or the
 * handset and the server would store the same customer differently.</p>
 */
class GhanaPhoneNumberTest {

    @Test
    fun `every way a Ghanaian number is written reduces to the same ten digits`() {
        listOf(
            "0244123456",
            "0244 123 456",
            "024-412-3456",
            "(024) 412 3456",
            "233244123456",
            "+233244123456",
            "+233 24 412 3456",
            "+233 0244 123456",
            "244123456"
        ).forEach { written ->
            assertThat(GhanaPhoneNumber.normalise(written)).isEqualTo("0244123456")
        }
    }

    @Test
    fun `every network's prefixes are accepted`() {
        listOf("020", "050", "024", "025", "053", "054", "055", "059", "026", "027", "056", "057", "023")
            .forEach { prefix -> assertThat(GhanaPhoneNumber.isValid("${prefix}1234567")).isTrue() }
    }

    @Test
    fun `things that are not Ghanaian mobile numbers are refused`() {
        listOf(
            "", "   ", "12345", "02441234567", "024412345", // wrong lengths
            "0302123456",                                   // an Accra landline
            "0211234567",                                   // no such mobile prefix
            "447911123456",                                 // a UK number
            "hello"
        ).forEach { input -> assertThat(GhanaPhoneNumber.normalise(input)).isNull() }
    }

    @Test
    fun `numbers are read back grouped`() {
        assertThat(GhanaPhoneNumber.display("233244123456")).isEqualTo("024 412 3456")
    }
}
