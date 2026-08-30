using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zazi.Infrastructure;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// The real API pipeline backed by real PostgreSQL.
/// </summary>
/// <remarks>
/// <see cref="ZaziApiFactory"/> runs on the InMemory provider, which is fine for
/// authorization but cannot enforce unique indexes, check constraints, or transactions.
/// Sync guarantees — idempotency under concurrency, atomic persistence, constraint-backed
/// duplicate detection — only mean something against a real database, so those tests use
/// this factory instead.
/// </remarks>
public sealed class PostgresApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    static PostgresApiFactory() => TestHostEnvironment.Apply();

    public PostgresApiFactory(string connectionString)
    {
        // Bound the pool explicitly. Several test classes each hold a factory, and the
        // suite deliberately drives 100-way concurrency, so the default of 100 connections
        // per data source exhausts PostgreSQL's max_connections and surfaces as "sorry, too
        // many clients already" — an artefact of the harness, not of the server under test.
        // See docs/TESTING.md for the max_connections the test server needs.
        _connectionString = new Npgsql.NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = 15,
            Timeout = 30,
            CommandTimeout = 60
        }.ConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();

            // Scoped, so each request gets its own context exactly as in production.
            services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(_connectionString));
        });
    }
}
