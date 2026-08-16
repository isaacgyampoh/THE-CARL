using Microsoft.EntityFrameworkCore;
using TheCarl.Application;
using TheCarl.Domain;
using TheCarl.Infrastructure;
using TheCarl.Infrastructure.Services;

namespace TheCarl.IntegrationTests.Postgres;

/// <summary>
/// Proves that concurrent submissions of the same transaction identity produce exactly one
/// ledger entry, enforced by PostgreSQL rather than by application checks.
/// </summary>
/// <remarks>
/// An application-level "does it already exist?" check cannot make this safe: two requests
/// can both read "no" before either commits. Correctness rests on the unique constraint, and
/// only a real database can demonstrate that.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class IdempotencyConcurrencyTests
{
    private readonly PostgresFixture _postgres;

    public IdempotencyConcurrencyTests(PostgresFixture postgres) => _postgres = postgres;

    [SkippableFact]
    public async Task TwoSimultaneousSubmissionsCreateExactlyOneTransaction()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var tenant = await SeedTenantAsync();
        var clientId = ClientTransactionId.Create("device-concurrency-a");

        var results = await SubmitConcurrentlyAsync(tenant, clientId, attempts: 2);

        await AssertExactlyOneAsync(tenant, clientId, results, expectedAttempts: 2);
    }

    [SkippableFact]
    public async Task TenSimultaneousSubmissionsCreateExactlyOneTransaction()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var tenant = await SeedTenantAsync();
        var clientId = ClientTransactionId.Create("device-concurrency-b");

        var results = await SubmitConcurrentlyAsync(tenant, clientId, attempts: 10);

        await AssertExactlyOneAsync(tenant, clientId, results, expectedAttempts: 10);
    }

    [SkippableFact]
    public async Task OneHundredSimultaneousSubmissionsCreateExactlyOneTransaction()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var tenant = await SeedTenantAsync();
        var clientId = ClientTransactionId.Create("device-concurrency-c");

        var results = await SubmitConcurrentlyAsync(tenant, clientId, attempts: 100);

        await AssertExactlyOneAsync(tenant, clientId, results, expectedAttempts: 100);
    }

    [SkippableFact]
    public async Task SequentialRetryAfterALostResponseIsAnIdempotentReplay()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        // The device posted, the server committed, and the response was lost on the way
        // back. The device retries the same client id after a reboot.
        var tenant = await SeedTenantAsync();
        var clientId = ClientTransactionId.Create("device-lost-response");

        var first = await SubmitAsync(tenant, clientId);
        var retry = await SubmitAsync(tenant, clientId);

        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(first.Amount, retry.Amount);

        await using var db = _postgres.CreateContext();
        Assert.Equal(1, await db.Transactions.CountAsync(x => x.ClientTransactionId == clientId));
    }

    [SkippableFact]
    public async Task DistinctClientIdsCreateDistinctTransactions()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        // Guards the opposite failure: over-eager deduplication losing real transactions.
        var tenant = await SeedTenantAsync();

        var ids = Enumerable.Range(0, 25)
            .Select(_ => ClientTransactionId.Create("device-distinct"))
            .ToList();

        foreach (var id in ids)
        {
            await SubmitAsync(tenant, id);
        }

        await using var db = _postgres.CreateContext();
        var count = await db.Transactions.CountAsync(x => x.OrganizationId == tenant.OrganizationId);
        Assert.Equal(25, count);
    }

    [SkippableFact]
    public async Task TheSameClientIdInTwoTenantsCreatesTwoTransactions()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        // Idempotency is scoped to the organization. Two tenants are independent ledgers,
        // so an id collision across them must not suppress either transaction.
        var alpha = await SeedTenantAsync();
        var beta = await SeedTenantAsync();
        var sharedId = ClientTransactionId.Create("device-shared-across-tenants");

        await SubmitAsync(alpha, sharedId);
        await SubmitAsync(beta, sharedId);

        await using var db = _postgres.CreateContext();
        Assert.Equal(1, await db.Transactions.CountAsync(
            x => x.OrganizationId == alpha.OrganizationId && x.ClientTransactionId == sharedId));
        Assert.Equal(1, await db.Transactions.CountAsync(
            x => x.OrganizationId == beta.OrganizationId && x.ClientTransactionId == sharedId));
    }

    [SkippableFact]
    public async Task ConcurrentSubmissionsLeaveTheBalanceProjectionConsistent()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var tenant = await SeedTenantAsync();
        var clientId = ClientTransactionId.Create("device-balance-consistency");

        await SubmitConcurrentlyAsync(tenant, clientId, attempts: 20);

        await using var db = _postgres.CreateContext();
        var cash = await db.CashBalances.SingleAsync(x => x.BranchId == tenant.BranchId);

        // One accepted cash-in of ₵100 means exactly ₵100 of cash movement, no matter how
        // many callers raced. A partially committed attempt would show up here.
        Assert.Equal(100m, cash.CurrentCash);
    }

    [SkippableFact]
    public async Task ANegativeAmountIsRejectedByTheDatabase()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var tenant = await SeedTenantAsync();

        await using var db = _postgres.CreateContext();
        db.Transactions.Add(new FinancialTransaction
        {
            OrganizationId = tenant.OrganizationId,
            BranchId = tenant.BranchId,
            AgentId = tenant.UserId,
            Network = "MTN",
            Type = TransactionType.CashIn,
            Amount = -50m,
            State = TransactionLifecycleState.Accepted
        });

        // CK_Transactions_AmountNonNegative: direction belongs to the type, so a negative
        // magnitude must never reach the table even if application validation is bypassed.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task AnUnknownTypedTransactionCannotCarryALedgerEffect()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var tenant = await SeedTenantAsync();

        await using var db = _postgres.CreateContext();
        db.Transactions.Add(new FinancialTransaction
        {
            OrganizationId = tenant.OrganizationId,
            BranchId = tenant.BranchId,
            AgentId = tenant.UserId,
            Network = "MTN",
            Type = TransactionType.Unknown,
            Amount = 100m,
            CashDelta = 100m,
            State = TransactionLifecycleState.PendingReview
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task AReversalWithoutAnOriginalIsRejectedByTheDatabase()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var tenant = await SeedTenantAsync();

        await using var db = _postgres.CreateContext();
        db.Transactions.Add(new FinancialTransaction
        {
            OrganizationId = tenant.OrganizationId,
            BranchId = tenant.BranchId,
            AgentId = tenant.UserId,
            Network = "MTN",
            Type = TransactionType.Reversal,
            Amount = 100m,
            State = TransactionLifecycleState.Accepted
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<TransactionDto?>> SubmitConcurrentlyAsync(
        TenantSeed tenant,
        string clientTransactionId,
        int attempts)
    {
        // A barrier makes the attempts contend, rather than trickling in as each task starts.
        using var barrier = new Barrier(attempts);

        var tasks = Enumerable.Range(0, attempts).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            try
            {
                return await SubmitAsync(tenant, clientTransactionId);
            }
            catch (Exception)
            {
                // A losing attempt may surface as a conflict; what matters is the row count.
                return (TransactionDto?)null;
            }
        })).ToArray();

        return await Task.WhenAll(tasks);
    }

    private async Task<TransactionDto> SubmitAsync(TenantSeed tenant, string clientTransactionId)
    {
        // Each submission gets its own context, as separate HTTP requests would.
        await using var db = _postgres.CreateContext();
        var service = new TransactionService(db, new LedgerService(db));

        return await service.CreateTransactionAsync(new CreateTransactionRequest(
            tenant.OrganizationId,
            tenant.BranchId,
            tenant.UserId,
            null,
            "MTN",
            TransactionType.CashIn,
            100m,
            Money.DefaultCurrency,
            "0241234567",
            "REF-CONCURRENCY",
            TransactionSource.Manual,
            "Concurrency probe",
            ClientTransactionId: clientTransactionId));
    }

    private async Task AssertExactlyOneAsync(
        TenantSeed tenant,
        string clientTransactionId,
        IReadOnlyList<TransactionDto?> results,
        int expectedAttempts)
    {
        await using var db = _postgres.CreateContext();

        var rows = await db.Transactions
            .Where(x => x.OrganizationId == tenant.OrganizationId && x.ClientTransactionId == clientTransactionId)
            .ToListAsync();

        Assert.Single(rows);

        var succeeded = results.Where(r => r is not null).ToList();
        Assert.NotEmpty(succeeded);

        // Every caller that got a result must have been handed the same accepted row: a
        // losing attempt resolves as an idempotent replay, not as a second transaction.
        Assert.All(succeeded, r => Assert.Equal(rows[0].Id, r!.Id));
        Assert.Equal(expectedAttempts, results.Count);
    }

    private sealed record TenantSeed(Guid OrganizationId, Guid BranchId, Guid UserId);

    private async Task<TenantSeed> SeedTenantAsync()
    {
        await using var db = _postgres.CreateContext();

        var organization = new Organization
        {
            Name = $"Concurrency {Guid.NewGuid():N}",
            Country = "GH",
            CurrencyCode = Money.DefaultCurrency
        };
        var branch = new Branch { OrganizationId = organization.Id, Name = "Main" };
        var user = new User
        {
            OrganizationId = organization.Id,
            BranchId = branch.Id,
            FullName = "Concurrency Agent",
            Email = $"agent-{Guid.NewGuid():N}@carl.test",
            IsActive = true
        };

        db.Organizations.Add(organization);
        db.Branches.Add(branch);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return new TenantSeed(organization.Id, branch.Id, user.Id);
    }
}
