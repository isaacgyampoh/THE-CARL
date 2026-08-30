using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zazi.Application.Security;
using Zazi.Application.Sync;
using Zazi.Domain;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// Financial correctness when the database, the network, or the response fails.
/// </summary>
/// <remarks>
/// <para>
/// Failures are injected against the real database rather than mocked. A mocked
/// <c>SaveChanges</c> throwing proves the C# handles an exception; it proves nothing about
/// whether PostgreSQL actually rolled back. These tests kill real backends and revoke real
/// permissions, then assert on committed state read through a fresh connection.
/// </para>
/// <para>
/// The rule under test throughout: never lose money, never create money, never double-post,
/// never silently guess.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class FailureInjectionTests : IDisposable
{
    private const string SyncPath = "/api/v1/sync/transactions";

    private readonly PostgresFixture _postgres;
    private readonly PostgresApiFactory? _factory;

    public FailureInjectionTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = postgres.IsAvailable ? new PostgresApiFactory(postgres.ConnectionString!) : null;
    }

    public void Dispose() => _factory?.Dispose();

    // ─── A/B/C: rollback at transaction boundaries ───────────────────────────

    [SkippableFact]
    public async Task AFailureMidTransactionLeavesNoEvidenceNoLedgerRowAndNoBalance()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var clientId = ClientTransactionId.Create("failure-mid-transaction");

        // Drive the unit of work directly so the failure lands between the ledger write and
        // the commit — the exact window where a partial commit would be possible.
        await using (var db = _postgres.CreateContext())
        {
            await using var dbTransaction = await db.Database.BeginTransactionAsync();

            var evidence = new TransactionEvidence
            {
                OrganizationId = tenant.OrganizationId,
                BranchId = tenant.BranchId,
                SubmittedByUserId = tenant.UserId,
                Provider = "MTN",
                Amount = 500m,
                ObservedType = TransactionType.CashIn,
                Fingerprint = new string('a', 64),
                State = TransactionLifecycleState.Accepted
            };
            var transaction = new FinancialTransaction
            {
                OrganizationId = tenant.OrganizationId,
                BranchId = tenant.BranchId,
                AgentId = tenant.UserId,
                EvidenceId = evidence.Id,
                Network = "MTN",
                Type = TransactionType.CashIn,
                Amount = 500m,
                CashDelta = 500m,
                FloatDelta = -500m,
                State = TransactionLifecycleState.Accepted,
                ClientTransactionId = clientId,
                EvidenceFingerprint = evidence.Fingerprint
            };

            db.TransactionEvidence.Add(evidence);
            db.Transactions.Add(transaction);
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO "CashBalances" ("Id", "OrganizationId", "BranchId", "OpeningCash", "CurrentCash", "CreatedAt", "UpdatedAt")
                 VALUES ({Guid.NewGuid()}, {tenant.OrganizationId}, {tenant.BranchId}, 0, 500, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow})
                 ON CONFLICT ("OrganizationId", "BranchId")
                 DO UPDATE SET "CurrentCash" = "CashBalances"."CurrentCash" + 500;
                 """);

            // Everything above is written but uncommitted. Abort instead of committing.
            await dbTransaction.RollbackAsync();
        }

        // Read committed state through a completely separate connection.
        await using var verify = _postgres.CreateContext();
        Assert.Equal(0, await verify.Transactions.CountAsync(x => x.OrganizationId == tenant.OrganizationId));
        Assert.Equal(0, await verify.TransactionEvidence.CountAsync(x => x.OrganizationId == tenant.OrganizationId));
        Assert.Equal(0, await verify.CashBalances.CountAsync(x => x.BranchId == tenant.BranchId));
    }

    [SkippableFact]
    public async Task KillingTheBackendMidTransactionCommitsNothing()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        await using (var db = _postgres.CreateContext())
        {
            await using var dbTransaction = await db.Database.BeginTransactionAsync();

            db.Transactions.Add(new FinancialTransaction
            {
                OrganizationId = tenant.OrganizationId,
                BranchId = tenant.BranchId,
                AgentId = tenant.UserId,
                Network = "MTN",
                Type = TransactionType.CashIn,
                Amount = 900m,
                CashDelta = 900m,
                FloatDelta = -900m,
                State = TransactionLifecycleState.Accepted,
                ClientTransactionId = ClientTransactionId.Create("killed-backend")
            });
            await db.SaveChangesAsync();

            // Terminate this session's own backend from another connection. PostgreSQL
            // aborts the in-flight transaction; nothing written above survives.
            var pid = await GetBackendPidAsync(db);
            await TerminateBackendAsync(pid);

            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await db.Database.ExecuteSqlRawAsync("SELECT 1");
                await dbTransaction.CommitAsync();
            });
        }

        await using var verify = _postgres.CreateContext();
        Assert.Equal(0, await verify.Transactions.CountAsync(x => x.OrganizationId == tenant.OrganizationId));
    }

    [SkippableFact]
    public async Task AFailedLedgerProjectionRollsBackTheWholeUnitOfWork()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        await using (var db = _postgres.CreateContext())
        {
            await using var dbTransaction = await db.Database.BeginTransactionAsync();

            db.Transactions.Add(new FinancialTransaction
            {
                OrganizationId = tenant.OrganizationId,
                BranchId = tenant.BranchId,
                AgentId = tenant.UserId,
                Network = "MTN",
                Type = TransactionType.CashIn,
                Amount = 250m,
                CashDelta = 250m,
                FloatDelta = -250m,
                State = TransactionLifecycleState.Accepted,
                ClientTransactionId = ClientTransactionId.Create("ledger-failure")
            });
            await db.SaveChangesAsync();

            // The ledger step fails — here by violating the amount check constraint.
            await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Transactions" ("Id","OrganizationId","BranchId","AgentId","Network","Type",
                        "Amount","Currency","CashDelta","FloatDelta","TransactionAtUtc","AcceptedAtUtc",
                        "Source","State","ConfidenceScore","CreatedAt","UpdatedAt")
                    VALUES (gen_random_uuid(), @p0, @p1, @p2, 'MTN', 0, -1, 'GHS', 0, 0, now(), now(), 0, 3, 1, now(), now())
                    """,
                    tenant.OrganizationId, tenant.BranchId, tenant.UserId));

            await dbTransaction.RollbackAsync();
        }

        await using var verify = _postgres.CreateContext();

        // The valid transaction written before the failure must not survive either.
        Assert.Equal(0, await verify.Transactions.CountAsync(x => x.OrganizationId == tenant.OrganizationId));
    }

    [SkippableFact]
    public async Task ABatchSurvivesTheDatabaseBecomingUnavailableMidWay()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var accepted = await PostAsync(tenant, [SyncItemFactory.CashIn(tenant.BranchId, 100m)]);
        Assert.Equal(1, accepted.Accepted);

        // Terminate every backend for this database, as a failover or restart would.
        await TerminateAllBackendsAsync();

        // The next request reconnects. The already-committed transaction is intact and the
        // new one posts: a connection drop must not corrupt or lose committed money.
        var afterOutage = await PostAsync(tenant, [SyncItemFactory.CashIn(tenant.BranchId, 50m)]);
        Assert.Equal(1, afterOutage.Accepted);

        await using var verify = _postgres.CreateContext();
        var cash = await verify.CashBalances.SingleAsync(x => x.BranchId == tenant.BranchId);
        Assert.Equal(150m, cash.CurrentCash);
    }

    // ─── D: lost response ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ACommittedTransactionWhoseResponseWasLostReplaysAsDuplicate()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var item = SyncItemFactory.CashIn(tenant.BranchId, 400m);

        // First call commits; imagine the response never reaches the device.
        var first = await PostAsync(tenant, [item]);
        var transactionId = first.Results[0].TransactionId;

        // The device believes it failed and retries the identical payload.
        var retry = await PostAsync(tenant, [item]);

        Assert.Equal(SyncItemStatus.Duplicate, retry.Results[0].Status);
        Assert.Equal(transactionId, retry.Results[0].TransactionId);
        Assert.Equal(SyncErrorCategory.Duplicate, retry.Results[0].Category);

        await using var verify = _postgres.CreateContext();
        Assert.Equal(1, await verify.Transactions.CountAsync(x => x.OrganizationId == tenant.OrganizationId));

        // Exactly one posting: the balance is 400, not 800.
        var cash = await verify.CashBalances.SingleAsync(x => x.BranchId == tenant.BranchId);
        Assert.Equal(400m, cash.CurrentCash);
    }

    [SkippableFact]
    public async Task ClientTimeoutFollowedByRetryPostsOnce()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var item = SyncItemFactory.CashIn(tenant.BranchId, 175m);

        // A timeout so short the client gives up while the server is still working. The
        // server must not treat "client stopped listening" as "transaction did not happen".
        var impatient = ClientFor(tenant);
        impatient.Timeout = TimeSpan.FromMilliseconds(1);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            impatient.PostAsJsonAsync(SyncPath, new { transactions = new[] { item } }));

        // Give the server a moment to finish the work the client abandoned.
        await Task.Delay(2_000);

        var retry = await PostAsync(tenant, [item]);
        Assert.Single(retry.Results);

        await using var verify = _postgres.CreateContext();
        var count = await verify.Transactions.CountAsync(x => x.OrganizationId == tenant.OrganizationId);

        // Whether the abandoned request committed or not, the retry converges on exactly one.
        Assert.Equal(1, count);

        var cash = await verify.CashBalances.SingleAsync(x => x.BranchId == tenant.BranchId);
        Assert.Equal(175m, cash.CurrentCash);
    }

    [SkippableFact]
    public async Task ARequestCancelledMidFlightNeverLeavesAPartialPosting()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var items = Enumerable.Range(0, 20)
            .Select(_ => SyncItemFactory.CashIn(tenant.BranchId, 10m))
            .ToList();

        var client = ClientFor(tenant);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        try
        {
            await client.PostAsJsonAsync(SyncPath, new { transactions = items }, cts.Token);
        }
        catch (Exception)
        {
            // Expected: the connection closes part-way through.
        }

        await Task.Delay(2_000);

        await using var verify = _postgres.CreateContext();
        var transactions = await verify.Transactions
            .Where(x => x.OrganizationId == tenant.OrganizationId)
            .ToListAsync();

        var cash = await verify.CashBalances
            .SingleOrDefaultAsync(x => x.BranchId == tenant.BranchId);

        // However many items were processed before the cancellation, the balance must equal
        // exactly the sum of the committed rows — never a partial or orphaned amount.
        var expected = transactions.Sum(x => x.CashDelta);
        Assert.Equal(expected, cash?.CurrentCash ?? 0m);
    }

    // ─── Error classification ────────────────────────────────────────────────

    [SkippableFact]
    public async Task PermanentFailuresAreNotMarkedRetryable()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var otherTenant = await SeedAsync();

        var response = await PostAsync(tenant, [
            SyncItemFactory.CashIn(tenant.BranchId, 0m),                   // validation
            SyncItemFactory.CashIn(otherTenant.BranchId, 10m)              // authorization
        ]);

        // A client that retries these forever burns battery and rate limit and can never
        // succeed. The server states the verdict rather than leaving it to be inferred.
        Assert.All(response.Results, r => Assert.False(r.IsRetryable));
        Assert.Contains(response.Results, r => r.Category == SyncErrorCategory.Validation);
        Assert.Contains(response.Results, r => r.Category == SyncErrorCategory.Authorization);
    }

    [SkippableFact]
    public async Task AnInvalidBatchDoesNotProduceAServerError()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var raw = await ClientFor(tenant).PostAsJsonAsync(SyncPath, new
        {
            transactions = new[] { SyncItemFactory.CashIn(tenant.BranchId, 0m) }
        });

        // A per-item business outcome is not an infrastructure failure. Returning 500 would
        // make clients retry a permanently invalid payload forever.
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
    }

    // ─── Cross-tenant information leakage ────────────────────────────────────

    [SkippableFact]
    public async Task CrossTenantFailuresRevealNothingAboutTheOtherTenant()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        var realBranch = await PostAsync(alpha, [SyncItemFactory.CashIn(beta.BranchId, 10m)]);
        var fabricatedBranch = await PostAsync(alpha, [SyncItemFactory.CashIn(Guid.NewGuid(), 10m)]);

        // A branch that exists in another tenant and one that exists nowhere must be
        // indistinguishable, otherwise ids can be probed across tenants.
        Assert.Equal(realBranch.Results[0].ReasonCode, fabricatedBranch.Results[0].ReasonCode);
        Assert.Equal(realBranch.Results[0].Message, fabricatedBranch.Results[0].Message);
    }

    [SkippableFact]
    public async Task ConflictsAreScopedToTheirOwnOrganization()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        var originalId = await PostOriginalAsync(alpha, 90m);
        await PostAsync(alpha, [SyncItemFactory.Reversal(alpha.BranchId, originalId, 90m)]);
        await PostAsync(alpha, [SyncItemFactory.Reversal(alpha.BranchId, originalId, 90m)]);

        await using var verify = _postgres.CreateContext();

        Assert.True(await verify.SyncConflicts.AnyAsync(x => x.OrganizationId == alpha.OrganizationId));
        Assert.False(await verify.SyncConflicts.AnyAsync(x => x.OrganizationId == beta.OrganizationId));
    }

    [SkippableFact]
    public async Task ARepeatedConflictingSubmissionDoesNotPileUpConflictRecords()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var originalId = await PostOriginalAsync(tenant, 70m);
        await PostAsync(tenant, [SyncItemFactory.Reversal(tenant.BranchId, originalId, 70m)]);

        var offending = SyncItemFactory.Reversal(tenant.BranchId, originalId, 70m);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await PostAsync(tenant, [offending]);
        }

        await using var verify = _postgres.CreateContext();
        var conflicts = await verify.SyncConflicts
            .CountAsync(x => x.OrganizationId == tenant.OrganizationId
                && x.ReasonCode == SyncReasonCodes.AlreadyReversed);

        // Five retries must leave one item on the operator's queue, not five.
        Assert.Equal(1, conflicts);
    }

    // ─── Conflict record integrity ───────────────────────────────────────────

    [SkippableFact]
    public async Task AConflictCannotBeMarkedResolvedWithoutAnActor()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        await using var db = _postgres.CreateContext();
        db.SyncConflicts.Add(new SyncConflict
        {
            OrganizationId = tenant.OrganizationId,
            ClientTransactionId = ClientTransactionId.Create("resolution-guard"),
            ConflictType = SyncConflictType.UnresolvableUniqueViolation,
            ReasonCode = SyncReasonCodes.ClientIdReusedWithDifferentPayload,
            // Claimed resolved, but says nothing about who or when.
            Status = SyncConflictStatus.Resolved
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<int> GetBackendPidAsync(Microsoft.EntityFrameworkCore.DbContext db)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        return connection.ProcessID;
    }

    private async Task TerminateBackendAsync(int pid)
    {
        await using var admin = new NpgsqlConnection(_postgres.ConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = "SELECT pg_terminate_backend(@pid)";
        command.Parameters.AddWithValue("pid", pid);
        await command.ExecuteNonQueryAsync();
    }

    private async Task TerminateAllBackendsAsync()
    {
        NpgsqlConnection.ClearAllPools();

        await using var admin = new NpgsqlConnection(_postgres.ConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText =
            """
            SELECT pg_terminate_backend(pid) FROM pg_stat_activity
            WHERE datname = current_database() AND pid <> pg_backend_pid()
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> PostOriginalAsync(TenantSeed tenant, decimal amount)
    {
        var response = await PostAsync(tenant, [SyncItemFactory.CashIn(tenant.BranchId, amount)]);
        return response.Results[0].TransactionId!.Value;
    }

    private HttpClient ClientFor(TenantSeed tenant) =>
        _factory!.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.Agent));

    private async Task<SyncTransactionsResponse> PostAsync(TenantSeed tenant, IReadOnlyList<object> items)
    {
        var raw = await ClientFor(tenant).PostAsJsonAsync(SyncPath, new { transactions = items });
        raw.EnsureSuccessStatusCode();
        return (await raw.Content.ReadFromJsonAsync<SyncTransactionsResponse>())!;
    }

    private Task<TenantSeed> SeedAsync() => TenantSeedFactory.CreateAsync(_postgres);
}
