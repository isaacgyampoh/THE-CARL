using DotNet.Testcontainers.Builders;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Zazi.Infrastructure;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// A disposable PostgreSQL database, migrated to the current schema.
/// </summary>
/// <remarks>
/// <para>
/// The EF InMemory provider enforces neither unique indexes nor check constraints and has
/// no transactions, so it cannot prove idempotency, atomicity, or constraint behaviour.
/// Anything that depends on the database actually saying "no" has to run here.
/// </para>
/// <para>
/// Two ways to supply a server, tried in order:
/// </para>
/// <list type="number">
/// <item><description>
/// <c>ZAZI_TEST_POSTGRES</c> — a connection string to an existing server. The fixture
/// creates its own uniquely-named database on it and drops it afterwards. This is the path
/// for machines without a container runtime.
/// </description></item>
/// <item><description>
/// Testcontainers, when a Docker-compatible socket is present. This is the CI path.
/// </description></item>
/// </list>
/// <para>
/// If neither is available the tests skip with a reason rather than passing silently.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Connection string to an already-running server, bypassing Testcontainers.</summary>
    public const string ExternalServerVariable = "ZAZI_TEST_POSTGRES";

    private PostgreSqlContainer? _container;
    private string? _externalAdminConnectionString;
    private string? _createdDatabaseName;

    /// <summary>Null when no PostgreSQL is available; tests skip rather than fail.</summary>
    public string? ConnectionString { get; private set; }

    public string? SkipReason { get; private set; }

    public bool IsAvailable => ConnectionString is not null;

    /// <summary>How the server was obtained, for reporting.</summary>
    public string Provenance { get; private set; } = "none";

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(ExternalServerVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            await InitializeFromExternalServerAsync(external);
            return;
        }

        if (!DockerIsReachable())
        {
            SkipReason =
                $"No PostgreSQL available. Either set {ExternalServerVariable} to a connection string, " +
                "or install a container runtime (Docker Desktop, Colima, or Podman) so Testcontainers " +
                "can start one. See docs/TESTING.md.";
            return;
        }

        await InitializeFromContainerAsync();
    }

    /// <summary>
    /// Caps what one connection string may hold open, so the suite's demand is bounded by the
    /// suite rather than by whatever <c>max_connections</c> the server happens to allow.
    /// </summary>
    /// <remarks>
    /// Most integration tests stand up a <c>WebApplicationFactory</c>, and each host builds
    /// its own Npgsql data source with its own pool. Nothing caps those pools by default, so
    /// a handful of overlapping hosts can reach for several hundred connections. A developer
    /// machine absorbs that; GitHub's <c>postgres:16</c> service container allows 100 and the
    /// integration suite failed there all of 21 Sep 2026 with <c>53300: sorry, too many
    /// clients already</c> — while passing locally, which is the worst way for a gate to fail.
    /// The short idle lifetime matters as much as the cap: connections left by a disposed host
    /// stay open against the server for five minutes by default, well past the end of the run.
    /// </remarks>
    private static string Bounded(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = 20,
            ConnectionIdleLifetime = 15
        }.ConnectionString;

    private async Task InitializeFromExternalServerAsync(string adminConnectionString)
    {
        try
        {
            // A dedicated database per fixture keeps concurrent test classes from colliding
            // on a shared server, and makes cleanup a single DROP.
            _externalAdminConnectionString = adminConnectionString;
            _createdDatabaseName = $"carl_tests_{Guid.NewGuid():N}";

            await using (var admin = new NpgsqlConnection(adminConnectionString))
            {
                await admin.OpenAsync();
                await using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE \"{_createdDatabaseName}\"";
                await create.ExecuteNonQueryAsync();
            }

            ConnectionString = Bounded(new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                Database = _createdDatabaseName
            }.ConnectionString);

            Provenance = $"external server ({ExternalServerVariable})";

            await using var db = CreateContext();
            await db.Database.MigrateAsync();
        }
        catch (Exception exception)
        {
            SkipReason = $"External PostgreSQL could not be prepared: {exception.Message}";
            ConnectionString = null;
        }
    }

    private async Task InitializeFromContainerAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("zazi_tests")
                .WithUsername("carl")
                .WithPassword("carl-test-password")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
                .Build();

            await _container.StartAsync();
            ConnectionString = Bounded(_container.GetConnectionString());
            Provenance = "Testcontainers (postgres:16-alpine)";

            await using var db = CreateContext();
            await db.Database.MigrateAsync();
        }
        catch (Exception exception)
        {
            SkipReason = $"PostgreSQL container could not be started: {exception.Message}";
            ConnectionString = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        if (_externalAdminConnectionString is null || _createdDatabaseName is null)
        {
            return;
        }

        try
        {
            // Pooled connections keep the database "in use" and block the DROP.
            NpgsqlConnection.ClearAllPools();

            await using var admin = new NpgsqlConnection(_externalAdminConnectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{_createdDatabaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }
        catch
        {
            // A leftover test database is untidy but must never fail a test run.
        }
    }

    /// <summary>A fresh context over the fixture's database. Callers own its lifetime.</summary>
    public ApplicationDbContext CreateContext()
    {
        if (ConnectionString is null)
        {
            throw new InvalidOperationException("PostgreSQL is not available for this run.");
        }

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new ApplicationDbContext(options);
    }

    /// <summary>
    /// Cheap probe for a Docker-compatible endpoint. Checking first avoids a long connect
    /// timeout followed by a nested exception when no runtime is installed at all.
    /// </summary>
    private static bool DockerIsReachable()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return true;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidateSockets =
        [
            "/var/run/docker.sock",
            Path.Combine(home, ".docker", "run", "docker.sock"),
            Path.Combine(home, ".colima", "default", "docker.sock"),
            Path.Combine(home, ".local", "share", "containers", "podman", "machine", "podman.sock")
        ];

        return candidateSockets.Any(File.Exists);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
