package app.zazi.core.data.repository

import app.zazi.core.data.network.ZaziApi
import java.io.File

/** The periods an agent can ask for, in the words on the button. */
enum class StatementRange(val wireName: String, val label: String) {
    TODAY("Today", "Today"),
    THIS_WEEK("ThisWeek", "This week"),
    THIS_MONTH("ThisMonth", "This month"),
    THIS_YEAR("ThisYear", "This year")
}

enum class StatementKind(val wireName: String, val label: String, val mimeType: String, val extension: String) {
    PDF("Pdf", "PDF", "application/pdf", "pdf"),
    EXCEL("Csv", "Excel", "text/csv", "csv")
}

sealed interface StatementDownload {
    data class Saved(val file: File, val mimeType: String) : StatementDownload

    /** No connection, or the server refused. Nothing was saved. */
    data class Failed(val reason: String) : StatementDownload
}

/**
 * Fetches the agent's own statement and saves it where it can be shared.
 *
 * <p>Needs a connection — statements are built on the server from the full ledger, which
 * includes transactions recorded on the agent's other phones. The file goes to a cache folder
 * that the FileProvider exposes one file at a time, and is replaced by the next download rather
 * than piling up.</p>
 */
class StatementRepository(
    private val api: ZaziApi,
    private val cacheDir: File
) {
    suspend fun download(range: StatementRange, kind: StatementKind): StatementDownload {
        val response = runCatching { api.statement(range.wireName, kind.wireName) }
            .getOrElse {
                return StatementDownload.Failed("No connection. Statements need data or Wi-Fi to download.")
            }

        if (!response.isSuccessful) {
            return StatementDownload.Failed("The statement could not be prepared (${response.code()}). Try again.")
        }

        val body = response.body()
            ?: return StatementDownload.Failed("The statement came back empty. Try again.")

        val folder = File(cacheDir, "statements").apply { mkdirs() }
        // Old statements are cleared first: a cache folder that only grows is how a phone with
        // little storage runs out of it.
        folder.listFiles()?.forEach { it.delete() }

        val name = fileNameFrom(response.headers()["Content-Disposition"])
            ?: "zazi-statement-${range.wireName.lowercase()}.${kind.extension}"
        val file = File(folder, name)

        body.byteStream().use { input -> file.outputStream().use { output -> input.copyTo(output) } }
        return StatementDownload.Saved(file, kind.mimeType)
    }

    /** The server's own name for the file, which carries the business and the dates. */
    private fun fileNameFrom(disposition: String?): String? {
        if (disposition.isNullOrBlank()) return null
        val match = Regex("""filename\*?=(?:UTF-8'')?"?([^";]+)"?""").find(disposition) ?: return null
        // Only the file name, never a path someone could have put in the header.
        return match.groupValues[1].substringAfterLast('/').substringAfterLast('\\').takeIf { it.isNotBlank() }
    }
}
