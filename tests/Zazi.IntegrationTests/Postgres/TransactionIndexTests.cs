using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// The indexes the hot read paths depend on.
/// </summary>
/// <remarks>
/// An index is invisible when it is missing: queries still return the right answers, just
/// slowly, and only under a data volume no test carries. Asserting it exists is the only
/// cheap way to notice that a model change quietly dropped it.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class TransactionIndexTests
{
    private readonly PostgresFixture _postgres;

    public TransactionIndexTests(PostgresFixture postgres) => _postgres = postgres;

    [SkippableFact]
    public async Task TransactionsAreIndexedByOrganizationAndDateTogether()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        await using var db = _postgres.CreateContext();
        await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync();

        // indisvalid matters as much as existence: a CONCURRENTLY build that fails leaves the
        // index in place and marked invalid, where it is not used by any query. That is the
        // failure this migration can actually have in production, and it looks like success.
        await using var command = new NpgsqlCommand(
            """
            SELECT i.indisvalid
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relname = 'IX_Transactions_OrganizationId_TransactionAtUtc';
            """,
            connection);

        var isValid = await command.ExecuteScalarAsync();

        Assert.True(isValid is bool, "The organization+date index on Transactions is missing.");
        Assert.True((bool)isValid!, "The index exists but is INVALID, so no query will use it.");
    }
}
