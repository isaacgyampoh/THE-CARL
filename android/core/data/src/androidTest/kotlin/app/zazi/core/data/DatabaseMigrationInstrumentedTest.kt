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
 * Every migration, against a real SQLite file.
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

    @Test
    fun migratingToTheLatestVersionKeepsRecordedMoneyAndAddsTheNewColumns() {
        val name = "migration-2-3-test.db"

        helper.createDatabase(name, 2).use { database ->
            // A transaction recorded before the upgrade, with real money on it.
            database.execSQL(
                """
                INSERT INTO local_transactions (
                    clientTransactionId, branchId, deviceId, provider, transactionType,
                    amountMinor, currency, transactionAtUtcMillis, deviceRecordedAtUtcMillis,
                    sourceType, parserVersion, fingerprint, cashDeltaMinor, floatDeltaMinor,
                    createdAtUtcMillis
                ) VALUES (
                    'CTX-BEFORE-BALANCES', 'branch', 'device', 'MTN', 'CASH_IN',
                    29500, 'GHS', 1000, 1000, 'ANDROID_SMS', 'mtn-v1', 'fp-1', 29500, -29500,
                    1000
                )
                """.trimIndent()
            )
        }

        val migrated = helper.runMigrationsAndValidate(name, 4, true, *ZaziDatabaseMigrations.ALL)

        // The money is untouched. This is the property that matters: a column added to a
        // table holding unsynced financial records must not disturb a single figure.
        migrated.query(
            "SELECT amountMinor, cashDeltaMinor, balanceAfterMinor FROM local_transactions"
        ).use { cursor ->
            assertThat(cursor.moveToFirst()).isTrue()
            assertThat(cursor.getInt(0)).isEqualTo(29500)
            assertThat(cursor.getInt(1)).isEqualTo(29500)
            // Nullable and null for rows captured before the column existed — "not known",
            // which the gap detector skips rather than reading as a balance of zero.
            assertThat(cursor.isNull(2)).isTrue()
        }

        migrated.query("SELECT balanceAfterMinor, customerName FROM transaction_evidence")
            .use { cursor -> assertThat(cursor.count).isEqualTo(0) }

        // The name column arrives empty for work captured before it existed — "not known",
        // never an empty string, which a blank search would match against every row.
        migrated.query("SELECT customerName FROM local_transactions").use { cursor ->
            assertThat(cursor.moveToFirst()).isTrue()
            assertThat(cursor.isNull(0)).isTrue()
        }
    }
}
