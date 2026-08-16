using Microsoft.EntityFrameworkCore;
using Npgsql;
using TheCarl.Infrastructure;

namespace TheCarl.IntegrationTests.Postgres;

/// <summary>
/// Proves a fresh deployment works: the complete migration chain applied to a genuinely
/// empty database, then the invariants that protect money verified against the result.
/// </summary>
/// <remarks>
/// Testing only against an already-migrated schema hides migrations that cannot actually
/// run — an index renamed after its column was dropped, a check constraint that existing
/// rows violate, a type PostgreSQL rejects. Both defects found in earlier phases were of
/// exactly that kind, so this creates its own database from nothing every run.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class FreshDeploymentTests
{
    private readonly PostgresFixture _postgres;

    public FreshDeploymentTests(PostgresFixture postgres) => _postgres = postgres;

    [SkippableFact]
    public async Task TheCompleteMigrationChainAppliesToAnEmptyDatabase()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        await using var database = await FreshDatabase.CreateAsync(_postgres.ConnectionString!);
        await using var db = database.CreateContext();

        // No EnsureCreated: the migration chain itself must be able to build the schema.
        await db.Database.MigrateAsync();

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();

        Assert.NotEmpty(applied);
        Assert.Empty(pending);
    }

    [SkippableFact]
    public async Task AFreshlyMigratedSchemaCarriesTheFinancialInvariants()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        await using var database = await FreshDatabase.CreateAsync(_postgres.ConnectionString!);
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();

        // The constraints that stop money being lost or created must exist on a brand new
        // deployment, not only on a database that happens to have been migrated in stages.
        var indexes = await QueryNamesAsync(database,
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public'");

        Assert.Contains("UX_Transactions_Organization_ClientTransactionId", indexes);
        Assert.Contains("UX_Transactions_Organization_EvidenceFingerprint", indexes);
        Assert.Contains("UX_Transactions_Organization_ReversesTransactionId", indexes);

        var checks = await QueryNamesAsync(database,
            "SELECT conname FROM pg_constraint WHERE contype = 'c'");

        Assert.Contains("CK_Transactions_AmountNonNegative", checks);
        Assert.Contains("CK_Transactions_UnknownHasNoLedgerEffect", checks);
        Assert.Contains("CK_Transactions_ReversalHasOriginal", checks);
        Assert.Contains("CK_SyncConflicts_ResolutionComplete", checks);

        var tables = await QueryNamesAsync(database,
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public'");

        Assert.Contains("Transactions", tables);
        Assert.Contains("TransactionEvidence", tables);
        Assert.Contains("SyncConflicts", tables);
        Assert.Contains("AuthSessions", tables);
        Assert.Contains("UserRoles", tables);

        // Replaced by TransactionEvidence in Phase 12; a fresh deployment must not recreate it.
        Assert.DoesNotContain("CapturedSmsMessages", tables);
    }

    [SkippableFact]
    public async Task MoneyColumnsAreNumericNeverFloatingPoint()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        await using var database = await FreshDatabase.CreateAsync(_postgres.ConnectionString!);
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT table_name, column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND (column_name IN ('Amount','CashDelta','FloatDelta','CurrentCash','CurrentFloat',
                                   'OpeningCash','OpeningFloat','ExpectedCash','ActualCash',
                                   'Difference','SubmittedAmount','Threshold',
                                   'WarningThreshold','CriticalThreshold'))
            """;

        var offenders = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var type = reader.GetString(2);
            if (type is not "numeric")
            {
                offenders.Add($"{reader.GetString(0)}.{reader.GetString(1)} is {type}");
            }
        }

        // Binary floating point cannot represent ₵0.10 exactly. A single double column in a
        // ledger is enough to make balances disagree with the transactions behind them.
        Assert.Empty(offenders);
    }

    private static async Task<HashSet<string>> QueryNamesAsync(FreshDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>An empty database created for one test and dropped afterwards.</summary>
    private sealed class FreshDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;

        private FreshDatabase(string adminConnectionString, string databaseName, string connectionString)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<FreshDatabase> CreateAsync(string adminConnectionString)
        {
            var name = $"carl_fresh_{Guid.NewGuid():N}";

            await using (var admin = new NpgsqlConnection(adminConnectionString))
            {
                await admin.OpenAsync();
                await using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE \"{name}\"";
                await create.ExecuteNonQueryAsync();
            }

            var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                Database = name
            }.ConnectionString;

            return new FreshDatabase(adminConnectionString, name, connectionString);
        }

        public ApplicationDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(ConnectionString)
                .Options);

        public async ValueTask DisposeAsync()
        {
            try
            {
                NpgsqlConnection.ClearAllPools();

                await using var admin = new NpgsqlConnection(_adminConnectionString);
                await admin.OpenAsync();
                await using var drop = admin.CreateCommand();
                drop.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
                await drop.ExecuteNonQueryAsync();
            }
            catch
            {
                // A leftover test database is untidy but must never fail a run.
            }
        }
    }
}
