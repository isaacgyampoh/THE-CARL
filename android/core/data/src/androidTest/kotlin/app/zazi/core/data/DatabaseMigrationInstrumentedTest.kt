package app.zazi.core.data

import androidx.room.testing.MigrationTestHelper
import androidx.sqlite.db.framework.FrameworkSQLiteOpenHelperFactory
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import app.zazi.core.data.database.ZaziDatabase
import app.zazi.core.data.database.ZaziDatabaseMigrations
import com.google.common.truth.Truth.assertThat
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The v1 to v2 migration, against a real SQLite file.
 *
 * <p>The property that matters is not that the new table appears — it is that an agent's
 * existing work is still there afterwards. A migration that silently dropped the outbox would
 * destroy transactions that never reached the server, and no other test would notice.</p>
 */
@RunWith(AndroidJUnit4::class)
class DatabaseMigrationInstrumentedTest {

    @get:Rule
    val helper = MigrationTestHelper(
        InstrumentationRegistry.getInstrumentation(),
        ZaziDatabase::class.java,
        emptyList(),
        FrameworkSQLiteOpenHelperFactory()
    )

    @Test
    fun migratingFromVersionOneKeepsQueuedWorkAndAddsTheTelemetryTable() {
        val name = "migration-test.db"

        helper.createDatabase(name, 1).use { database ->
            // A transaction captured before the upgrade, still waiting to be sent.
            database.execSQL(
                """
                INSERT INTO outbox_items (
                    clientTransactionId, state, attemptCount, nextAttemptAtUtcMillis,
                    createdAtUtcMillis, updatedAtUtcMillis
                ) VALUES ('CTX-BEFORE-UPGRADE', 'PENDING', 0, 0, 0, 0)
                """.trimIndent()
            )
        }

        val migrated = helper.runMigrationsAndValidate(name, 2, true, *ZaziDatabaseMigrations.ALL)

        migrated.query("SELECT clientTransactionId FROM outbox_items").use { cursor ->
            assertThat(cursor.moveToFirst()).isTrue()
            assertThat(cursor.getString(0)).isEqualTo("CTX-BEFORE-UPGRADE")
        }

        // And the new table exists and is usable.
        migrated.query("SELECT COUNT(*) FROM telemetry_events").use { cursor ->
            assertThat(cursor.moveToFirst()).isTrue()
            assertThat(cursor.getInt(0)).isEqualTo(0)
        }
    }
}
