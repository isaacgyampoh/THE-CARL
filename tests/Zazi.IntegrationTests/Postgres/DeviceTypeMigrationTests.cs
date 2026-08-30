using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Zazi.Domain;
using Zazi.Infrastructure;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// Proves the Phase 1 migration is safe on a fresh database <b>and</b> on one that already
/// contains devices.
/// </summary>
/// <remarks>
/// Testing only against a fresh schema would hide the case that actually matters: existing
/// enrolled handsets. A blanket default would leave every one of them as
/// <see cref="DeviceType.Other"/> and silently strip their SMS-capture capability, because
/// capabilities are granted from this column.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class DeviceTypeMigrationTests
{
    /// <summary>The migration under test. Everything before it forms the "existing" database.</summary>
    private const string PhaseOneMigration = "Phase1DeviceTypeAndCapabilities";

    private readonly PostgresFixture _postgres;

    public DeviceTypeMigrationTests(PostgresFixture postgres) => _postgres = postgres;

    [SkippableFact]
    public async Task TheMigrationAppliesToAFreshDatabase()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        await using var database = await ScratchDatabase.CreateAsync(_postgres.ConnectionString!);
        await using var db = database.CreateContext();

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [SkippableFact]
    public async Task ExistingDevicesAreBackfilledFromTheirLegacyPlatformString()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        await using var database = await ScratchDatabase.CreateAsync(_postgres.ConnectionString!);

        // Migrate to the state immediately BEFORE Phase 1, so the devices below are genuinely
        // pre-existing rows rather than ones created against the new schema.
        var priorMigration = await MigrateToStateBeforePhaseOneAsync(database);
        Skip.If(priorMigration is null, "Phase 1 migration not found in the chain.");

        var organizationId = await SeedLegacyDevicesAsync(database);

        // Now apply Phase 1 over the top.
        await using (var upgraded = database.CreateContext())
        {
            await upgraded.Database.MigrateAsync();
        }

        await using var db = database.CreateContext();
        var devices = await db.Devices.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .ToDictionaryAsync(x => x.DeviceIdentifier, x => x.DeviceType);

        // Every handset keeps a platform that reflects what it actually is.
        Assert.Equal(DeviceType.AndroidPhone, devices["legacy-android"]);
        Assert.Equal(DeviceType.AndroidPhone, devices["legacy-android-lower"]);
        Assert.Equal(DeviceType.AndroidPhone, devices["legacy-android-versioned"]);
        Assert.Equal(DeviceType.AndroidTablet, devices["legacy-android-tablet"]);
        Assert.Equal(DeviceType.iPhone, devices["legacy-iphone"]);
        Assert.Equal(DeviceType.iPad, devices["legacy-ipad"]);
        Assert.Equal(DeviceType.WebBrowser, devices["legacy-web"]);
        Assert.Equal(DeviceType.GsmGateway, devices["legacy-gateway"]);

        // Unrecognised platforms stay Other rather than being guessed at.
        Assert.Equal(DeviceType.Other, devices["legacy-unknown"]);
        Assert.Equal(DeviceType.Other, devices["legacy-empty"]);

        // KaiOS contains "ios" but is a feature phone. Backfilling it as an iPhone would
        // hand it iOS capabilities it does not have.
        Assert.Equal(DeviceType.Other, devices["legacy-kaios"]);
    }

    [SkippableFact]
    public async Task NoExistingDeviceIsLostByTheMigration()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        await using var database = await ScratchDatabase.CreateAsync(_postgres.ConnectionString!);
        var priorMigration = await MigrateToStateBeforePhaseOneAsync(database);
        Skip.If(priorMigration is null, "Phase 1 migration not found in the chain.");

        var organizationId = await SeedLegacyDevicesAsync(database);

        int countBefore;
        await using (var before = database.CreateContext())
        {
            countBefore = await before.Devices.CountAsync(x => x.OrganizationId == organizationId);
        }

        await using (var upgraded = database.CreateContext())
        {
            await upgraded.Database.MigrateAsync();
        }

        await using var db = database.CreateContext();
        var countAfter = await db.Devices.CountAsync(x => x.OrganizationId == organizationId);

        // The migration is additive. Losing a device row would orphan its transactions.
        Assert.Equal(countBefore, countAfter);
        Assert.Equal(11, countAfter);
    }

    [SkippableFact]
    public async Task TheLegacyPlatformColumnSurvivesTheMigration()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        await using var database = await ScratchDatabase.CreateAsync(_postgres.ConnectionString!);
        var priorMigration = await MigrateToStateBeforePhaseOneAsync(database);
        Skip.If(priorMigration is null, "Phase 1 migration not found in the chain.");

        var organizationId = await SeedLegacyDevicesAsync(database);

        await using (var upgraded = database.CreateContext())
        {
            await upgraded.Database.MigrateAsync();
        }

        await using var db = database.CreateContext();
        var device = await db.Devices.AsNoTracking()
            .SingleAsync(x => x.OrganizationId == organizationId && x.DeviceIdentifier == "legacy-android");

        // Platform is deprecated, not removed: existing clients may still read it.
        Assert.Equal("Android", device.Platform);
        Assert.Equal(DeviceType.AndroidPhone, device.DeviceType);
    }

    [SkippableFact]
    public async Task TheDatabaseRejectsAnOutOfRangeDeviceType()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        await using var database = await ScratchDatabase.CreateAsync(_postgres.ConnectionString!);
        await using (var db = database.CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        var (organizationId, branchId) = await SeedOrganizationAsync(database);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        // Bypasses the C# enum entirely, as a raw SQL path or a defective future migration
        // would. The database must still refuse it.
        command.CommandText =
            """
            INSERT INTO "Devices"
              ("Id","OrganizationId","BranchId","Name","DeviceIdentifier","Platform","DeviceType",
               "Role","Status","Network","AppVersion","OsVersion","IsRevoked","LastSeenAt",
               "CreatedAt","UpdatedAt")
            VALUES (gen_random_uuid(), @org, @branch, 'Rogue', 'rogue-device', 'Android', 99,
                    0, 1, 'MTN', '1.0.0', 'Android 14', false, now(), now(), now())
            """;
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("branch", branchId);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal("23514", exception.SqlState); // check_violation
        Assert.Contains("CK_Devices_DeviceTypeInRange", exception.Message);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public async Task TheDatabaseAcceptsEveryDefinedDeviceType(int deviceType)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        await using var database = await ScratchDatabase.CreateAsync(_postgres.ConnectionString!);
        await using (var db = database.CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        var (organizationId, branchId) = await SeedOrganizationAsync(database);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "Devices"
              ("Id","OrganizationId","BranchId","Name","DeviceIdentifier","Platform","DeviceType",
               "Role","Status","Network","AppVersion","OsVersion","IsRevoked","LastSeenAt",
               "CreatedAt","UpdatedAt")
            VALUES (gen_random_uuid(), @org, @branch, 'Valid', @identifier, 'Android', @type,
                    0, 1, 'MTN', '1.0.0', 'Android 14', false, now(), now(), now())
            """;
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("branch", branchId);
        command.Parameters.AddWithValue("identifier", $"valid-{deviceType}");
        command.Parameters.AddWithValue("type", deviceType);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Migrates to the migration immediately preceding Phase 1, so seeded rows are genuinely
    /// "existing data" from the schema's point of view.
    /// </summary>
    private static async Task<string?> MigrateToStateBeforePhaseOneAsync(ScratchDatabase database)
    {
        await using var db = database.CreateContext();

        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.Contains(PhaseOneMigration, StringComparison.Ordinal));
        if (index <= 0)
        {
            return null;
        }

        var previous = all[index - 1];
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);

        return previous;
    }

    /// <summary>
    /// Seeds an organization and a branch. Devices carry a foreign key to Branches, so a
    /// fabricated branch id would fail on the FK before the DeviceType check is ever reached.
    /// </summary>
    private static async Task<(Guid OrganizationId, Guid BranchId)> SeedOrganizationAsync(ScratchDatabase database)
    {
        await using var db = database.CreateContext();

        var organization = new Organization
        {
            Name = $"Migration {Guid.NewGuid():N}",
            Country = "GH",
            CurrencyCode = Money.DefaultCurrency
        };
        var branch = new Branch { OrganizationId = organization.Id, Name = "Main" };

        db.Organizations.Add(organization);
        db.Branches.Add(branch);
        await db.SaveChangesAsync();

        return (organization.Id, branch.Id);
    }

    /// <summary>
    /// Inserts devices through raw SQL against the pre-Phase-1 schema, which has no
    /// DeviceType column. EF cannot be used here — its model already knows about the column.
    /// </summary>
    private static async Task<Guid> SeedLegacyDevicesAsync(ScratchDatabase database)
    {
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        await using (var org = connection.CreateCommand())
        {
            org.CommandText =
                """
                INSERT INTO "Organizations"
                  ("Id","Name","Country","CurrencyCode","MfaPolicy","CreatedAt","UpdatedAt")
                VALUES (@id, 'Legacy Org', 'GH', 'GHS', 0, now(), now());

                INSERT INTO "Branches" ("Id","OrganizationId","Name","CreatedAt","UpdatedAt")
                VALUES (@branch, @id, 'Legacy Branch', now(), now());
                """;
            org.Parameters.AddWithValue("id", organizationId);
            org.Parameters.AddWithValue("branch", branchId);
            await org.ExecuteNonQueryAsync();
        }

        // Platform strings as they appear in real enrolments, plus the awkward cases.
        (string Identifier, string Platform)[] legacyDevices =
        [
            ("legacy-android", "Android"),
            ("legacy-android-lower", "android"),
            ("legacy-android-versioned", "Android 14"),
            ("legacy-android-tablet", "Android Tablet"),
            ("legacy-iphone", "iPhone"),
            ("legacy-ipad", "iPad"),
            ("legacy-web", "WebBrowser"),
            ("legacy-gateway", "GsmGateway"),
            ("legacy-unknown", "Symbian"),
            ("legacy-empty", ""),
            ("legacy-kaios", "KaiOS")
        ];

        foreach (var (identifier, platform) in legacyDevices)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO "Devices"
                  ("Id","OrganizationId","BranchId","Name","DeviceIdentifier","Platform",
                   "Role","Status","Network","AppVersion","OsVersion","IsRevoked","LastSeenAt",
                   "CreatedAt","UpdatedAt")
                VALUES (gen_random_uuid(), @org, @branch, @name, @identifier, @platform,
                        1, 1, 'MTN', '1.0.0', 'unknown', false, now(), now(), now())
                """;
            insert.Parameters.AddWithValue("org", organizationId);
            insert.Parameters.AddWithValue("branch", branchId);
            insert.Parameters.AddWithValue("name", identifier);
            insert.Parameters.AddWithValue("identifier", identifier);
            insert.Parameters.AddWithValue("platform", platform);
            await insert.ExecuteNonQueryAsync();
        }

        return organizationId;
    }

    /// <summary>A throwaway database created per test and dropped afterwards.</summary>
    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;

        private ScratchDatabase(string admin, string name, string connectionString)
        {
            _adminConnectionString = admin;
            _databaseName = name;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<ScratchDatabase> CreateAsync(string adminConnectionString)
        {
            var name = $"carl_mig_{Guid.NewGuid():N}";

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

            return new ScratchDatabase(adminConnectionString, name, connectionString);
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
