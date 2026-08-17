using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TheCarl.Application.Security;
using TheCarl.Application.Sync;
using TheCarl.Domain;
using TheCarl.Infrastructure;

namespace TheCarl.IntegrationTests.Postgres;

/// <summary>
/// <c>POST /api/v1/sync/transactions</c> over real HTTP against real PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public class BatchSyncTests : IDisposable
{
    private const string SyncPath = "/api/v1/sync/transactions";

    private readonly PostgresFixture _postgres;
    private readonly PostgresApiFactory? _factory;

    public BatchSyncTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = postgres.IsAvailable ? new PostgresApiFactory(postgres.ConnectionString!) : null;
    }

    public void Dispose() => _factory?.Dispose();

    /// <summary>
    /// One client per tenant, reused across concurrent requests. Creating a client per
    /// request multiplies connection pools and exhausts the server; a real device holds one
    /// client for its lifetime.
    /// </summary>
    private HttpClient ClientFor(TenantSeed tenant) =>
        _factory!.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, CarlRoles.Agent));

    // ─── Batch sizes ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ASingleTransactionIsAccepted()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var response = await PostAsync(tenant, [Item(tenant)]);

        Assert.Equal(1, response.Submitted);
        Assert.Equal(1, response.Accepted);
        Assert.Equal(SyncItemStatus.Accepted, response.Results[0].Status);
        Assert.NotNull(response.Results[0].TransactionId);
    }

    [SkippableTheory]
    [InlineData(10)]
    [InlineData(100)]
    public async Task ABatchOfManyTransactionsIsAcceptedInFull(int count)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var items = Enumerable.Range(0, count).Select(_ => Item(tenant)).ToList();
        var response = await PostAsync(tenant, items);

        Assert.Equal(count, response.Accepted);
        Assert.Equal(0, response.Rejected);
        await AssertLedgerRowCountAsync(tenant, count);
    }

    [SkippableFact]
    public async Task ABatchAboveTheConfiguredMaximumIsRefusedWhole()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var items = Enumerable.Range(0, 101).Select(_ => Item(tenant)).ToList();
        var raw = await RawPostAsync(tenant, items);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, raw.StatusCode);

        // Refused whole, never truncated: a truncated batch would silently lose the tail.
        await AssertLedgerRowCountAsync(tenant, 0);
    }

    // ─── Idempotency and retry ───────────────────────────────────────────────

    [SkippableFact]
    public async Task ResubmittingTheSameBatchProducesNoNewTransactions()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var items = Enumerable.Range(0, 10).Select(_ => Item(tenant)).ToList();

        var first = await PostAsync(tenant, items);
        var second = await PostAsync(tenant, items);

        Assert.Equal(10, first.Accepted);
        Assert.Equal(10, second.Duplicate);
        Assert.Equal(0, second.Accepted);
        await AssertLedgerRowCountAsync(tenant, 10);
    }

    [SkippableFact]
    public async Task RetryingAHundredTimesRemainsDeterministic()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var items = new[] { Item(tenant, amount: 250m) };

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var response = await PostAsync(tenant, items);
            Assert.Single(response.Results);
            Assert.Equal(attempt == 0 ? SyncItemStatus.Accepted : SyncItemStatus.Duplicate,
                response.Results[0].Status);
        }

        await AssertLedgerRowCountAsync(tenant, 1);

        // The balance must reflect one posting, not a hundred.
        await using var db = _postgres.CreateContext();
        var cash = await db.CashBalances.SingleAsync(x => x.BranchId == tenant.BranchId);
        Assert.Equal(250m, cash.CurrentCash);
    }

    [SkippableFact]
    public async Task ADuplicateResultCarriesTheOriginalTransactionId()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var items = new[] { Item(tenant) };

        var first = await PostAsync(tenant, items);
        var second = await PostAsync(tenant, items);

        // The client needs the server id to finish reconciling its outbox row.
        Assert.Equal(first.Results[0].TransactionId, second.Results[0].TransactionId);
        Assert.Equal(SyncReasonCodes.DuplicateClientTransactionId, second.Results[0].ReasonCode);
    }

    [SkippableFact]
    public async Task TwoDevicesObservingOneEventProduceOneTransaction()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        // Same real-world event, different submission identities.
        var occurred = DateTimeOffset.UtcNow.AddMinutes(-30);
        var deviceA = Item(tenant, amount: 400m, occurredAt: occurred, reference: "SHAREDREF");
        var deviceB = Item(tenant, amount: 400m, occurredAt: occurred, reference: "SHAREDREF");

        var first = await PostAsync(tenant, [deviceA]);
        var second = await PostAsync(tenant, [deviceB]);

        Assert.Equal(SyncItemStatus.Accepted, first.Results[0].Status);
        Assert.Equal(SyncItemStatus.Duplicate, second.Results[0].Status);
        Assert.Equal(SyncReasonCodes.DuplicateEvidenceFingerprint, second.Results[0].ReasonCode);
        await AssertLedgerRowCountAsync(tenant, 1);
    }

    // ─── Concurrency ─────────────────────────────────────────────────────────

    [SkippableTheory]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(100)]
    public async Task ConcurrentRequestsForOneTransactionPostItOnce(int concurrency)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var item = Item(tenant, amount: 75m);

        var client = ClientFor(tenant);
        using var barrier = new Barrier(concurrency);
        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await PostAsync(tenant, [item], client);
        })).ToArray();

        var responses = await Task.WhenAll(tasks);

        Assert.Equal(concurrency, responses.Length);
        Assert.Equal(1, responses.Sum(r => r.Accepted));
        Assert.Equal(concurrency - 1, responses.Sum(r => r.Duplicate));
        await AssertLedgerRowCountAsync(tenant, 1);

        await using var db = _postgres.CreateContext();
        var cash = await db.CashBalances.SingleAsync(x => x.BranchId == tenant.BranchId);
        Assert.Equal(75m, cash.CurrentCash);
    }

    [SkippableFact]
    public async Task OneHundredDistinctTransactionsSubmittedConcurrentlyAllPost()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var items = Enumerable.Range(0, 100).Select(_ => Item(tenant, amount: 1m)).ToList();

        var client = ClientFor(tenant);
        using var barrier = new Barrier(items.Count);
        var tasks = items.Select(item => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await PostAsync(tenant, [item], client);
        })).ToArray();

        var responses = await Task.WhenAll(tasks);

        // The mirror of the previous test: deduplication must not swallow real transactions.
        Assert.Equal(100, responses.Sum(r => r.Accepted));
        await AssertLedgerRowCountAsync(tenant, 100);

        await using var db = _postgres.CreateContext();
        var cash = await db.CashBalances.SingleAsync(x => x.BranchId == tenant.BranchId);
        Assert.Equal(100m, cash.CurrentCash);
    }

    [SkippableFact]
    public async Task ConcurrentBatchesAcrossTenantsStayIsolated()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        var alphaClient = ClientFor(alpha);
        var betaClient = ClientFor(beta);
        using var barrier = new Barrier(2);
        var alphaTask = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await PostAsync(alpha, Enumerable.Range(0, 50).Select(_ => Item(alpha, amount: 2m)).ToList(), alphaClient);
        });
        var betaTask = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await PostAsync(beta, Enumerable.Range(0, 50).Select(_ => Item(beta, amount: 3m)).ToList(), betaClient);
        });

        await Task.WhenAll(alphaTask, betaTask);

        await AssertLedgerRowCountAsync(alpha, 50);
        await AssertLedgerRowCountAsync(beta, 50);

        await using var db = _postgres.CreateContext();
        Assert.Equal(100m, (await db.CashBalances.SingleAsync(x => x.BranchId == alpha.BranchId)).CurrentCash);
        Assert.Equal(150m, (await db.CashBalances.SingleAsync(x => x.BranchId == beta.BranchId)).CurrentCash);
    }

    // ─── Partial success ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AMixedBatchResolvesEachItemIndependently()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var alreadySent = Item(tenant);
        await PostAsync(tenant, [alreadySent]);

        var batch = new List<SyncTransactionApiItemDto>
        {
            Item(tenant),                                          // accepted
            Item(tenant),                                          // accepted
            alreadySent,                                           // duplicate
            Item(tenant) with { Amount = 0m },                     // rejected: below minimum
            Item(tenant) with { TransactionType = TransactionType.Unknown } // rejected: unclassified
        };

        var response = await PostAsync(tenant, batch);

        Assert.Equal(5, response.Submitted);
        Assert.Equal(2, response.Accepted);
        Assert.Equal(1, response.Duplicate);
        Assert.Equal(2, response.Rejected);

        // One bad item must never block the good ones.
        await AssertLedgerRowCountAsync(tenant, 3);

        Assert.Contains(response.Results, r => r.ReasonCode == SyncReasonCodes.InvalidAmount);
        Assert.Contains(response.Results, r => r.ReasonCode == SyncReasonCodes.UnknownTypeRequiresReview);
        Assert.All(response.Results, r => Assert.False(string.IsNullOrWhiteSpace(r.ClientTransactionId)));
    }

    // ─── Validation ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AFutureDatedTransactionIsRejected()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var response = await PostAsync(tenant,
            [Item(tenant, occurredAt: DateTimeOffset.UtcNow.AddHours(2))]);

        Assert.Equal(SyncReasonCodes.TimestampInFuture, response.Results[0].ReasonCode);
        await AssertLedgerRowCountAsync(tenant, 0);
    }

    [SkippableFact]
    public async Task ModestClockDriftIsTolerated()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        // A handset running a minute fast must not lose the agent a real transaction.
        var response = await PostAsync(tenant,
            [Item(tenant, occurredAt: DateTimeOffset.UtcNow.AddMinutes(1))]);

        Assert.Equal(SyncItemStatus.Accepted, response.Results[0].Status);
    }

    [SkippableFact]
    public async Task AnAncientTransactionIsRejected()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var response = await PostAsync(tenant,
            [Item(tenant, occurredAt: DateTimeOffset.UtcNow.AddDays(-200))]);

        Assert.Equal(SyncReasonCodes.TimestampTooOld, response.Results[0].ReasonCode);
    }

    [SkippableFact]
    public async Task ALongOfflineBacklogStillPosts()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        // Forty-eight hours offline is normal for an agent in poor coverage.
        var items = Enumerable.Range(0, 20)
            .Select(i => Item(tenant, occurredAt: DateTimeOffset.UtcNow.AddHours(-48 + i)))
            .ToList();

        var response = await PostAsync(tenant, items);

        Assert.Equal(20, response.Accepted);
    }

    [SkippableFact]
    public async Task AMalformedClientTransactionIdIsRejected()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var response = await PostAsync(tenant, [Item(tenant) with { ClientTransactionId = "not-a-valid-id" }]);

        Assert.Equal(SyncReasonCodes.InvalidClientTransactionId, response.Results[0].ReasonCode);
    }

    [SkippableFact]
    public async Task SmsSourcedEvidenceWithoutAParserVersionIsRejected()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var response = await PostAsync(tenant, [
            Item(tenant) with { SourceType = EvidenceSourceType.AndroidSms, ParserVersion = null }
        ]);

        Assert.Equal(SyncReasonCodes.MissingParserVersion, response.Results[0].ReasonCode);
    }

    [SkippableFact]
    public async Task AnAdjustmentWithoutExplicitDeltasIsRejected()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var response = await PostAsync(tenant, [
            Item(tenant) with { TransactionType = TransactionType.Adjustment, CorrectionReason = "Till recount" }
        ]);

        Assert.Equal(SyncReasonCodes.AdjustmentRequiresExplicitDeltas, response.Results[0].ReasonCode);
    }

    [SkippableFact]
    public async Task AReversalWithoutAnOriginalIsRejected()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var response = await PostAsync(tenant, [
            Item(tenant) with { TransactionType = TransactionType.Reversal }
        ]);

        Assert.Equal(SyncReasonCodes.ReversalRequiresOriginal, response.Results[0].ReasonCode);
    }

    [SkippableFact]
    public async Task AReversalOfAnAcceptedTransactionExactlyOffsetsIt()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var original = await PostAsync(tenant, [Item(tenant, amount: 600m)]);
        var originalId = original.Results[0].TransactionId!.Value;

        var reversal = await PostAsync(tenant, [
            Item(tenant, amount: 600m) with
            {
                TransactionType = TransactionType.Reversal,
                ReversesTransactionId = originalId,
                CorrectionReason = "Customer cancelled"
            }
        ]);

        Assert.Equal(SyncItemStatus.Accepted, reversal.Results[0].Status);

        await using var db = _postgres.CreateContext();
        var cash = await db.CashBalances.SingleAsync(x => x.BranchId == tenant.BranchId);
        var networkFloat = await db.FloatBalances.SingleAsync(x => x.BranchId == tenant.BranchId);

        // Original plus reversal nets to zero on both sides, and the original row survives.
        Assert.Equal(0m, cash.CurrentCash);
        Assert.Equal(0m, networkFloat.CurrentFloat);
        Assert.Equal(2, await db.Transactions.CountAsync(x => x.OrganizationId == tenant.OrganizationId));
    }

    // ─── Authorization ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnAnonymousCallerIsRejected()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var client = _factory!.CreateClient();
        var response = await client.PostAsJsonAsync(SyncPath,
            new { transactions = new[] { Item(tenant) } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertLedgerRowCountAsync(tenant, 0);
    }

    [SkippableFact]
    public async Task SynchronisingIntoAnotherTenantsBranchIsRejected()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        var response = await PostAsync(alpha, [Item(alpha) with { BranchId = beta.BranchId }]);

        Assert.Equal(SyncReasonCodes.BranchNotInTenant, response.Results[0].ReasonCode);
        await AssertLedgerRowCountAsync(beta, 0);
    }

    [SkippableFact]
    public async Task SynchronisingWithAnotherTenantsDeviceIsRejected()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        var response = await PostAsync(alpha, [Item(alpha) with { DeviceId = beta.DeviceId }]);

        Assert.Equal(SyncReasonCodes.DeviceNotInTenant, response.Results[0].ReasonCode);
    }

    [SkippableFact]
    public async Task ARevokedDeviceCannotSynchronise()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        await using (var db = _postgres.CreateContext())
        {
            var device = await db.Devices.SingleAsync(x => x.Id == tenant.DeviceId);
            device.IsRevoked = true;
            device.Status = DeviceStatus.Revoked;
            await db.SaveChangesAsync();
        }

        var response = await PostAsync(tenant, [Item(tenant) with { DeviceId = tenant.DeviceId }]);

        Assert.Equal(SyncReasonCodes.DeviceRevoked, response.Results[0].ReasonCode);
        await AssertLedgerRowCountAsync(tenant, 0);
    }

    [SkippableFact]
    public async Task AnAgentCannotSynchroniseIntoAnotherBranchOfTheirOwnOrganization()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var otherBranchId = await CreateBranchAsync(tenant.OrganizationId, "Second Branch");
        var response = await PostAsync(tenant, [Item(tenant) with { BranchId = otherBranchId }]);

        Assert.Equal(SyncReasonCodes.BranchNotInTenant, response.Results[0].ReasonCode);
    }

    [SkippableFact]
    public async Task AnotherTenantsSessionIsRejected()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var alpha = await SeedAsync();
        var beta = await SeedAsync();
        var betaSession = await CreateSessionAsync(beta);

        var response = await PostAsync(alpha, [Item(alpha) with { SessionId = betaSession }]);

        Assert.Equal(SyncReasonCodes.SessionNotInTenant, response.Results[0].ReasonCode);
    }

    // ─── Session-close rule ──────────────────────────────────────────────────

    [SkippableFact]
    public async Task WorkCapturedBeforeSessionCloseStillPostsAfterIt()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var openedAt = DateTimeOffset.UtcNow.AddHours(-6);
        var closedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var sessionId = await CreateSessionAsync(tenant, openedAt, closedAt);

        // Captured mid-session; the device only reconnects now, after close.
        var response = await PostAsync(tenant, [
            Item(tenant, occurredAt: openedAt.AddHours(2)) with { SessionId = sessionId }
        ]);

        // Rejecting this would discard a genuine record for being slow to sync.
        Assert.Equal(SyncItemStatus.Accepted, response.Results[0].Status);
    }

    [SkippableFact]
    public async Task WorkDatedOutsideTheSessionWindowIsRejected()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var openedAt = DateTimeOffset.UtcNow.AddHours(-6);
        var closedAt = DateTimeOffset.UtcNow.AddHours(-4);
        var sessionId = await CreateSessionAsync(tenant, openedAt, closedAt);

        var response = await PostAsync(tenant, [
            Item(tenant, occurredAt: closedAt.AddHours(1)) with { SessionId = sessionId }
        ]);

        Assert.Equal(SyncReasonCodes.SessionNotOpenAtEventTime, response.Results[0].ReasonCode);
    }

    // ─── Ordering ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OutOfOrderArrivalProducesTheSameBalances()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var earlier = Item(tenant, amount: 100m, occurredAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var later = Item(tenant, amount: 40m, occurredAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        // The device reconnects and sends the later event first.
        await PostAsync(tenant, [later]);
        await PostAsync(tenant, [earlier]);

        await using var db = _postgres.CreateContext();
        var cash = await db.CashBalances.SingleAsync(x => x.BranchId == tenant.BranchId);

        // Projection is additive over stored deltas, so arrival order cannot change it.
        Assert.Equal(140m, cash.CurrentCash);
    }

    // ─── Performance ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task CatchUpThroughputIsRecorded()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        // Not a threshold assertion — machine-dependent timings make brittle tests. This
        // records a repeatable figure and asserts only that the work completes correctly.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var totalAccepted = 0;

        for (var batch = 0; batch < 10; batch++)
        {
            var items = Enumerable.Range(0, 100).Select(_ => Item(tenant, amount: 1m)).ToList();
            totalAccepted += (await PostAsync(tenant, items)).Accepted;
        }

        stopwatch.Stop();

        Assert.Equal(1_000, totalAccepted);
        await AssertLedgerRowCountAsync(tenant, 1_000);

        Console.WriteLine(
            $"[PERF] 1000 transactions across 10 batches of 100: {stopwatch.ElapsedMilliseconds} ms " +
            $"({1000_000.0 / Math.Max(1, stopwatch.ElapsedMilliseconds):F0} tx/s)");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Mirrors the API request shape so tests exercise real JSON binding.</summary>
    private sealed record SyncTransactionApiItemDto
    {
        public required string ClientTransactionId { get; init; }
        public required TransactionType TransactionType { get; init; }
        public required decimal Amount { get; init; }
        public required string Provider { get; init; }
        public required DateTimeOffset TransactionTimestamp { get; init; }
        public DateTimeOffset? DeviceReceivedAt { get; init; }
        public Guid? BranchId { get; init; }
        public Guid? DeviceId { get; init; }
        public Guid? SessionId { get; init; }
        public string? Currency { get; init; }
        public string? CustomerPhone { get; init; }
        public string? TransactionReference { get; init; }
        public string? EvidenceFingerprint { get; init; }
        public string? ParserVersion { get; init; }
        public EvidenceSourceType SourceType { get; init; }
        public Guid? ReversesTransactionId { get; init; }
        public decimal? AdjustmentCashDelta { get; init; }
        public decimal? AdjustmentFloatDelta { get; init; }
        public string? CorrectionReason { get; init; }
        public string? Notes { get; init; }
    }

    private static SyncTransactionApiItemDto Item(
        TenantSeed tenant,
        decimal amount = 100m,
        DateTimeOffset? occurredAt = null,
        string? reference = null) =>
        new()
        {
            ClientTransactionId = ClientTransactionId.Create($"device-{tenant.BranchId:N}"),
            TransactionType = TransactionType.CashIn,
            Amount = amount,
            Provider = "MTN",
            TransactionTimestamp = occurredAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            DeviceReceivedAt = occurredAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            BranchId = tenant.BranchId,
            SourceType = EvidenceSourceType.ManualEntry,
            ParserVersion = "manual-v1",
            // A unique reference by default keeps distinct test transactions from colliding
            // on the evidence fingerprint, which is scoped to the organization.
            TransactionReference = reference ?? Guid.NewGuid().ToString("N")[..12]
        };

    // ─── Wire format ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task StatusAndCategoryAreSerialisedByNameAsTheContractDocuments()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var raw = await RawPostAsync(tenant, [Item(tenant)]);
        raw.EnsureSuccessStatusCode();

        var body = await raw.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var result = document.RootElement.GetProperty("results")[0];

        // docs/BATCH_SYNC.md publishes `"status": "Accepted"`. Asserted on the raw payload
        // rather than a deserialised record on purpose: every other test in this file reads
        // into SyncTransactionsResponse, which accepts both the numeric and the named form,
        // so they all stayed green while the wire format silently broke the Android client.
        Assert.Equal(JsonValueKind.String, result.GetProperty("status").ValueKind);
        Assert.Equal(nameof(SyncItemStatus.Accepted), result.GetProperty("status").GetString());

        Assert.Equal(JsonValueKind.String, result.GetProperty("category").ValueKind);
        Assert.Equal(nameof(SyncErrorCategory.None), result.GetProperty("category").GetString());
    }

    [SkippableFact]
    public async Task ADuplicateReportsTheNamedStatusSoAClientCanStopRetrying()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var item = Item(tenant);
        await PostAsync(tenant, [item]);

        var raw = await RawPostAsync(tenant, [item]);
        raw.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await raw.Content.ReadAsStringAsync());
        var result = document.RootElement.GetProperty("results")[0];

        // A client that cannot read this value retries an already-posted transaction forever.
        Assert.Equal(nameof(SyncItemStatus.Duplicate), result.GetProperty("status").GetString());
    }

    private async Task<SyncTransactionsResponse> PostAsync(
        TenantSeed tenant,
        IReadOnlyList<SyncTransactionApiItemDto> items,
        HttpClient? client = null)
    {
        var raw = await RawPostAsync(tenant, items, client);
        raw.EnsureSuccessStatusCode();
        return (await raw.Content.ReadFromJsonAsync<SyncTransactionsResponse>())!;
    }

    private async Task<HttpResponseMessage> RawPostAsync(
        TenantSeed tenant,
        IReadOnlyList<SyncTransactionApiItemDto> items,
        HttpClient? client = null)
    {
        client ??= ClientFor(tenant);
        return await client.PostAsJsonAsync(SyncPath, new { transactions = items });
    }

    private async Task AssertLedgerRowCountAsync(TenantSeed tenant, int expected)
    {
        await using var db = _postgres.CreateContext();
        var count = await db.Transactions.CountAsync(x => x.OrganizationId == tenant.OrganizationId);
        Assert.Equal(expected, count);
    }

    private sealed record TenantSeed(Guid OrganizationId, Guid BranchId, Guid UserId, Guid DeviceId);

    private async Task<TenantSeed> SeedAsync()
    {
        await using var db = _postgres.CreateContext();

        var organization = new Organization
        {
            Name = $"Sync {Guid.NewGuid():N}",
            Country = "GH",
            CurrencyCode = Money.DefaultCurrency
        };
        var branch = new Branch { OrganizationId = organization.Id, Name = "Main" };
        var user = new User
        {
            OrganizationId = organization.Id,
            BranchId = branch.Id,
            FullName = "Sync Agent",
            Email = $"agent-{Guid.NewGuid():N}@carl.test",
            IsActive = true
        };
        var device = new Device
        {
            OrganizationId = organization.Id,
            BranchId = branch.Id,
            Name = "Agent Phone",
            DeviceIdentifier = $"device-{Guid.NewGuid():N}",
            Status = DeviceStatus.Active
        };

        db.Organizations.Add(organization);
        db.Branches.Add(branch);
        db.Users.Add(user);
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        return new TenantSeed(organization.Id, branch.Id, user.Id, device.Id);
    }

    private async Task<Guid> CreateBranchAsync(Guid organizationId, string name)
    {
        await using var db = _postgres.CreateContext();
        var branch = new Branch { OrganizationId = organizationId, Name = $"{name} {Guid.NewGuid():N}" };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        return branch.Id;
    }

    private async Task<Guid> CreateSessionAsync(
        TenantSeed tenant,
        DateTimeOffset? openedAt = null,
        DateTimeOffset? closedAt = null)
    {
        await using var db = _postgres.CreateContext();
        var session = new Session
        {
            OrganizationId = tenant.OrganizationId,
            BranchId = tenant.BranchId,
            UserId = tenant.UserId,
            DeviceId = tenant.DeviceId,
            OpenedAt = openedAt ?? DateTimeOffset.UtcNow.AddHours(-2),
            ClosedAt = closedAt,
            IsClosed = closedAt is not null,
            OpeningCash = 0m,
            OpeningFloat = 0m
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }
}
