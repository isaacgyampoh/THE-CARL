package app.zazi.core.data.database

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.sqlite.db.SupportSQLiteOpenHelper
import app.zazi.core.domain.security.DatabaseKeyProvider
import net.zetetic.database.sqlcipher.SupportOpenHelperFactory

/**
 * Zazi's local database.
 *
 * <p>Holds operational data only: evidence, local transactions, the outbox and sync history.
 * <b>No credentials.</b> Tokens live in Keystore-encrypted preferences, so a database
 * disclosure never yields authentication material.</p>
 *
 * <p>The schema is exported to <c>core/data/schemas</c> so migrations are tested against
 * real historical schemas rather than assumed correct.</p>
 */
@Database(
    entities = [
        EvidenceEntity::class,
        LocalTransactionEntity::class,
        OutboxItemEntity::class,
        SyncAttemptEntity::class,
        TelemetryEventEntity::class
    ],
    version = ZaziDatabase.VERSION,
    exportSchema = true
)
abstract class ZaziDatabase : RoomDatabase() {

    abstract fun evidenceDao(): EvidenceDao
    abstract fun localTransactionDao(): LocalTransactionDao
    abstract fun outboxDao(): OutboxDao
    abstract fun syncAttemptDao(): SyncAttemptDao
    abstract fun captureDao(): CaptureDao
    abstract fun telemetryDao(): TelemetryDao

    companion object {
        const val VERSION = 6
        const val DATABASE_NAME = "zazi.db"

        /**
         * Opens the encrypted database.
         *
         * <p>The passphrase comes from [DatabaseKeyProvider], which generates it once per
         * installation and wraps it with Keystore-backed key material. It is never in source
         * or configuration.</p>
         *
         * <p>Note SQLCipher zeroes the passphrase array it is handed, so the caller must not
         * reuse it afterwards.</p>
         */
        fun encrypted(
            context: Context,
            keyProvider: DatabaseKeyProvider
        ): ZaziDatabase {
            // net.zetetic:sqlcipher-android, not the legacy android-database-sqlcipher.
            // The old artifact ships native libraries aligned to 4 KB pages, which Android
            // 15 and later reject: on a 16 KB-page device libsqlcipher.so does not load, the
            // encrypted database cannot be opened, and the app is dead before its first
            // frame. Same SQLCipher 4 format, so a database written by the old library opens
            // unchanged here.
            System.loadLibrary("sqlcipher")
            return build(context, SupportOpenHelperFactory(keyProvider.databaseKey()))
        }

        /**
         * Opens with a caller-supplied helper factory.
         *
         * <p>Exists so tests can substitute the framework's unencrypted factory: SQLCipher
         * ships native libraries for Android ABIs and cannot load under a JVM test runner.
         * Schema, DAO and migration behaviour are therefore verified on the JVM, while the
         * encryption wiring is covered by instrumentation tests.</p>
         *
         * <p>Production always uses [encrypted].</p>
         */
        fun build(
            context: Context,
            openHelperFactory: SupportSQLiteOpenHelper.Factory?
        ): ZaziDatabase =
            Room.databaseBuilder(context, ZaziDatabase::class.java, DATABASE_NAME)
                .apply { openHelperFactory?.let { openHelperFactory(it) } }
                // No fallbackToDestructiveMigration: this database holds an agent's unsynced
                // work. Dropping it on a schema change would silently destroy transactions
                // that have never reached the server. Every version bump needs a real
                // migration and a test.
                .addMigrations(*ZaziDatabaseMigrations.ALL)
                .build()
    }
}

/**
 * Schema migrations.
 *
 * <p>Empty at version 1, and deliberately present so the next schema change has an obvious
 * home rather than reaching for destructive migration.</p>
 *
 * <p>Rules: additive where possible; never drop a column holding unsynced financial data;
 * every migration gets a test that seeds the old schema, migrates, and asserts the rows
 * survived with correct values.</p>
 */
object ZaziDatabaseMigrations {

    /**
     * Adds the telemetry queue.
     *
     * <p>Purely additive: no existing table is touched, so an upgrade cannot disturb evidence,
     * transactions or the outbox. A destructive fallback is deliberately not configured — a
     * migration that "fixes" itself by deleting the database would delete an agent's unsynced
     * work.</p>
     */
    private val MIGRATION_1_2 = object : androidx.room.migration.Migration(1, 2) {
        override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
            db.execSQL(
                """
                CREATE TABLE IF NOT EXISTS telemetry_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                    eventType TEXT NOT NULL,
                    severity TEXT NOT NULL,
                    status TEXT,
                    errorCode TEXT,
                    details TEXT,
                    durationMillis INTEGER,
                    correlationId TEXT,
                    occurredAtUtcMillis INTEGER NOT NULL
                )
                """.trimIndent()
            )
        }
    }

    /**
     * Records the balance the provider stated after each transaction.
     *
     * <p>Additive and nullable, so every row already stored keeps its value of "not known"
     * and nothing has to be recomputed. The column is what makes a missed message findable:
     * MTN states a running balance on every confirmation, so if two consecutive messages
     * disagree by more than the transactions between them, one went missing — and the
     * difference is exactly how much it was for.</p>
     */
    private val MIGRATION_2_3 = object : androidx.room.migration.Migration(2, 3) {
        override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
            db.execSQL("ALTER TABLE local_transactions ADD COLUMN balanceAfterMinor INTEGER")
            db.execSQL("ALTER TABLE transaction_evidence ADD COLUMN balanceAfterMinor INTEGER")
        }
    }

    /**
     * Records the counterparty's registered name beside their number.
     *
     * <p>The name the network confirms on the agent's screen when they dial a number. A
     * customer coming back to query a transaction remembers seeing their own name; they
     * frequently cannot say which number was used. Additive and nullable, so rows captured
     * before this keep "not known" rather than an empty name that would match a blank search.</p>
     */
    private val MIGRATION_3_4 = object : androidx.room.migration.Migration(3, 4) {
        override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
            db.execSQL("ALTER TABLE local_transactions ADD COLUMN customerName TEXT")
            db.execSQL("ALTER TABLE transaction_evidence ADD COLUMN customerName TEXT")
        }
    }

    /**
     * Records each message's own hash, so identity no longer depends on the parser.
     *
     * <p>Additive and nullable. Rows stored before this have no hash and keep being matched
     * the old way; everything captured from here on is recognisable as the same message
     * however the parser later changes its mind about what it means.</p>
     */
    private val MIGRATION_4_5 = object : androidx.room.migration.Migration(4, 5) {
        override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
            db.execSQL("ALTER TABLE transaction_evidence ADD COLUMN rawHash TEXT")
            db.execSQL(
                "CREATE INDEX IF NOT EXISTS index_transaction_evidence_rawHash " +
                    "ON transaction_evidence (rawHash)"
            )
        }
    }

    /** Remembers which unreadable wordings have already been sent for a parser fix. */
    private val MIGRATION_5_6 = object : androidx.room.migration.Migration(5, 6) {
        override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
            db.execSQL("ALTER TABLE transaction_evidence ADD COLUMN reportedAtUtcMillis INTEGER")
        }
    }

    val ALL: Array<androidx.room.migration.Migration> =
        arrayOf(MIGRATION_1_2, MIGRATION_2_3, MIGRATION_3_4, MIGRATION_4_5, MIGRATION_5_6)
}
