using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Zazi.Application.Security;
using Zazi.Application.Sync;
using Zazi.Domain;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// One original transaction may have at most one effective reversal.
/// </summary>
/// <remarks>
/// Reversing twice creates money that never existed. The guarantee is a partial unique
/// index on <c>(OrganizationId, ReversesTransactionId)</c>, not an application check: two
/// concurrent reversals can both read "not yet reversed" before either commits.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class ReversalSafetyTests : IDisposable
{
    private const string SyncPath = "/api/v1/sync/transactions";

    private readonly PostgresFixture _postgres;
    private readonly PostgresApiFactory? _factory;

    public ReversalSafetyTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = postgres.IsAvailable ? new PostgresApiFactory(postgres.ConnectionString!) : null;
    }

    public void Dispose() => _factory?.Dispose();

    [SkippableFact]
    public async Task AReversalExactlyOffsetsTheOriginalAndLeavesItIntact()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var originalId = await PostOriginalAsync(tenant, 500m);
        var reversal = await PostAsync(tenant, [Reversal(tenant, originalId, 500m)]);

        Assert.Equal(SyncItemStatus.Accepted, reversal.Results[0].Status);

        await using var db = _postgres.CreateContext();
        var cash = await db.CashBalances.SingleAsync(x => x.BranchId == tenant.BranchId);
        var networkFloat = await db.FloatBalances.SingleAsync(x => x.BranchId == tenant.BranchId);

        // Cash-in was +500 / -500; the reversal is exactly the inverse.
        Assert.Equal(0m, cash.CurrentCash);
        Assert.Equal(0m, networkFloat.CurrentFloat);

        // The original is untouched. Corrections never mutate history.
        var original = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == originalId);
        Assert.Equal(500m, original.Amount);
        Assert.Equal(TransactionType.CashIn, original.Type);
        Assert.Equal(500m, original.CashDelta);
        Assert.Equal(-500m, original.FloatDelta);
    }

    [SkippableFact]
    public async Task TheSameReversalSubmittedTwiceIsAnIdempotentReplay()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var originalId = await PostOriginalAsync(tenant, 300m);
        var reversal = Reversal(tenant, originalId, 300m);

        var first = await PostAsync(tenant, [reversal]);
        var second = await PostAsync(tenant, [reversal]);

        Assert.Equal(SyncItemStatus.Accepted, first.Results[0].Status);
        Assert.Equal(SyncItemStatus.Duplicate, second.Results[0].Status);

        await AssertReversalCountAsync(tenant, originalId, 1);
    }

    [SkippableFact]
    public async Task ASecondReversalWithADifferentClientIdIsRefused()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var originalId = await PostOriginalAsync(tenant, 800m);

        // Different submission identity, same original. Idempotency on ClientTransactionId
        // alone would not catch this — which is exactly the gap this test guards.
        await PostAsync(tenant, [Reversal(tenant, originalId, 800m)]);
        var second = await PostAsync(tenant, [Reversal(tenant, originalId, 800m)]);

        Assert.Equal(SyncItemStatus.Conflict, second.Results[0].Status);
        Assert.Equal(SyncReasonCodes.AlreadyReversed, second.Results[0].ReasonCode);
        Assert.Equal(SyncErrorCategory.Conflict, second.Results[0].Category);
        Assert.False(second.Results[0].IsRetryable);

        await AssertReversalCountAsync(tenant, originalId, 1);

        await using var db = _postgres.CreateContext();
        var cash = await db.CashBalances.SingleAsync(x => x.BranchId == tenant.BranchId);

        // One reversal, so the net is zero — not −800 as a double reversal would produce.
        Assert.Equal(0m, cash.CurrentCash);
    }

    [SkippableFact]
    public async Task ARefusedSecondReversalRaisesADurableConflict()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var originalId = await PostOriginalAsync(tenant, 120m);
        await PostAsync(tenant, [Reversal(tenant, originalId, 120m)]);
        var second = await PostAsync(tenant, [Reversal(tenant, originalId, 120m)]);

        Assert.NotNull(second.Results[0].ConflictId);

        await using var db = _postgres.CreateContext();
        var conflict = await db.SyncConflicts.AsNoTracking()
            .SingleAsync(x => x.Id == second.Results[0].ConflictId!.Value);

        // A conflict must never vanish: it is the operator's queue.
        Assert.Equal(SyncConflictStatus.Open, conflict.Status);
        Assert.Equal(SyncConflictType.DuplicateReversalAttempt, conflict.ConflictType);
        Assert.Equal(originalId, conflict.RelatedTransactionId);
        Assert.Equal(tenant.OrganizationId, conflict.OrganizationId);
        Assert.Null(conflict.ResolvedAtUtc);
    }

    [SkippableFact]
    public async Task OneHundredConcurrentReversalsProduceExactlyOne()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var originalId = await PostOriginalAsync(tenant, 1_000m);

        // Each attempt carries a distinct ClientTransactionId, so only the database-level
        // reversal invariant can stop them.
        var attempts = Enumerable.Range(0, 100).Select(_ => Reversal(tenant, originalId, 1_000m)).ToList();

        var client = ClientFor(tenant);
        using var barrier = new Barrier(attempts.Count);
        var tasks = attempts.Select(attempt => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await PostAsync(tenant, [attempt], client);
        })).ToArray();

        var responses = await Task.WhenAll(tasks);

        Assert.Equal(1, responses.Sum(r => r.Accepted));
        Assert.Equal(99, responses.Sum(r => r.Conflict));
        await AssertReversalCountAsync(tenant, originalId, 1);

        await using var db = _postgres.CreateContext();
        var cash = await db.CashBalances.SingleAsync(x => x.BranchId == tenant.BranchId);

        // The decisive assertion: 100 concurrent reversals must not create 99 × ₵1,000.
        Assert.Equal(0m, cash.CurrentCash);
    }

    [SkippableFact]
    public async Task ReversingAnAlreadyReversedTransactionIsRefusedAtTheDatabaseLevel()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var originalId = await PostOriginalAsync(tenant, 45m);
        await PostAsync(tenant, [Reversal(tenant, originalId, 45m)]);

        // Bypass the API entirely: prove the constraint holds even if application logic
        // were bypassed or defective.
        await using var db = _postgres.CreateContext();
        db.Transactions.Add(new FinancialTransaction
        {
            OrganizationId = tenant.OrganizationId,
            BranchId = tenant.BranchId,
            AgentId = tenant.UserId,
            Network = "MTN",
            Type = TransactionType.Reversal,
            Amount = 45m,
            CashDelta = -45m,
            FloatDelta = 45m,
            ReversesTransactionId = originalId,
            State = TransactionLifecycleState.Accepted,
            ClientTransactionId = ClientTransactionId.Create("rogue-writer")
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task ReversalsOfDifferentOriginalsAreUnaffected()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        // The constraint must not over-reach: distinct originals each get their reversal.
        var firstOriginal = await PostOriginalAsync(tenant, 10m);
        var secondOriginal = await PostOriginalAsync(tenant, 20m);

        var response = await PostAsync(tenant, [
            Reversal(tenant, firstOriginal, 10m),
            Reversal(tenant, secondOriginal, 20m)
        ]);

        Assert.Equal(2, response.Accepted);
        await AssertReversalCountAsync(tenant, firstOriginal, 1);
        await AssertReversalCountAsync(tenant, secondOriginal, 1);
    }

    [SkippableFact]
    public async Task TheSameOriginalIdInTwoTenantsIsNotConfused()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        var alphaOriginal = await PostOriginalAsync(alpha, 60m);
        var betaOriginal = await PostOriginalAsync(beta, 60m);

        await PostAsync(alpha, [Reversal(alpha, alphaOriginal, 60m)]);
        var betaReversal = await PostAsync(beta, [Reversal(beta, betaOriginal, 60m)]);

        // The index leads with OrganizationId, so tenants never contend with each other.
        Assert.Equal(SyncItemStatus.Accepted, betaReversal.Results[0].Status);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task AssertReversalCountAsync(TenantSeed tenant, Guid originalId, int expected)
    {
        await using var db = _postgres.CreateContext();
        var count = await db.Transactions.CountAsync(
            x => x.OrganizationId == tenant.OrganizationId && x.ReversesTransactionId == originalId);
        Assert.Equal(expected, count);
    }

    private async Task<Guid> PostOriginalAsync(TenantSeed tenant, decimal amount)
    {
        var response = await PostAsync(tenant, [SyncItemFactory.CashIn(tenant.BranchId, amount)]);
        Assert.Equal(SyncItemStatus.Accepted, response.Results[0].Status);
        return response.Results[0].TransactionId!.Value;
    }

    private static object Reversal(TenantSeed tenant, Guid originalId, decimal amount) =>
        SyncItemFactory.Reversal(tenant.BranchId, originalId, amount);

    private HttpClient ClientFor(TenantSeed tenant) =>
        _factory!.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.Agent));

    private async Task<SyncTransactionsResponse> PostAsync(
        TenantSeed tenant,
        IReadOnlyList<object> items,
        HttpClient? client = null)
    {
        client ??= ClientFor(tenant);
        var raw = await client.PostAsJsonAsync(SyncPath, new { transactions = items });
        raw.EnsureSuccessStatusCode();
        return (await raw.Content.ReadFromJsonAsync<SyncTransactionsResponse>())!;
    }

    private Task<TenantSeed> SeedAsync() => TenantSeedFactory.CreateAsync(_postgres);
}
