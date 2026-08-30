using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zazi.Application.Evidence;
using Zazi.Domain;
using Zazi.Infrastructure.Evidence;
using Zazi.Infrastructure.Services;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// <see cref="ManualEntryEvidenceSource"/> against real PostgreSQL.
/// </summary>
/// <remarks>
/// This is the capture path every platform falls back to and the only one iOS and Web have.
/// The rule it must never break: an evidence source produces evidence, and money moves only
/// through <see cref="LedgerPolicy"/> — never directly from a capture.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class EvidenceSourceTests
{
    private readonly PostgresFixture _postgres;

    public EvidenceSourceTests(PostgresFixture postgres) => _postgres = postgres;

    [SkippableFact]
    public async Task ManualCaptureProducesEvidenceAndAnAcceptedTransaction()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var result = await CaptureAsync(tenant, TransactionType.CashIn, 500m);

        Assert.Equal(TransactionLifecycleState.Accepted, result.State);
        Assert.NotNull(result.FinancialTransactionId);
        Assert.False(result.IsDuplicate);

        await using var db = _postgres.CreateContext();

        var evidence = await db.TransactionEvidence.AsNoTracking()
            .SingleAsync(x => x.Id == result.EvidenceId);

        // Manual entry is a person's account, never disguised as parsed provider evidence.
        Assert.Equal(EvidenceSourceType.ManualEntry, evidence.SourceType);
        Assert.Equal(tenant.OrganizationId, evidence.OrganizationId);

        var transaction = await db.Transactions.AsNoTracking()
            .SingleAsync(x => x.Id == result.FinancialTransactionId!.Value);

        Assert.Equal(TransactionSource.Manual, transaction.Source);
        Assert.Equal(evidence.Id, transaction.EvidenceId);
    }

    [SkippableFact]
    public async Task TheLedgerMovementComesFromLedgerPolicyNotFromTheCapture()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        await CaptureAsync(tenant, TransactionType.CashIn, 500m);

        await using var db = _postgres.CreateContext();
        var cash = await db.CashBalances.AsNoTracking().SingleAsync(x => x.BranchId == tenant.BranchId);
        var networkFloat = await db.FloatBalances.AsNoTracking().SingleAsync(x => x.BranchId == tenant.BranchId);

        // Cash-in: the agent receives physical cash and sends e-money. The capture had no
        // say in the direction.
        Assert.Equal(500m, cash.CurrentCash);
        Assert.Equal(-500m, networkFloat.CurrentFloat);
    }

    [SkippableFact]
    public async Task AnUnknownTypeIsHeldForReviewAndMovesNoMoney()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var result = await CaptureAsync(tenant, TransactionType.Unknown, 500m);

        Assert.Equal(TransactionLifecycleState.PendingReview, result.State);
        Assert.Null(result.FinancialTransactionId);

        await using var db = _postgres.CreateContext();

        // Evidence is preserved — the observation is real — but nothing posted.
        Assert.Equal(1, await db.TransactionEvidence.CountAsync(x => x.OrganizationId == tenant.OrganizationId));
        Assert.Equal(0, await db.Transactions.CountAsync(x => x.OrganizationId == tenant.OrganizationId));
        Assert.False(await db.CashBalances.AnyAsync(x => x.BranchId == tenant.BranchId));
    }

    [SkippableFact]
    public async Task AReversalCaptureIsHeldRatherThanPosted()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        // A reversal needs a referenced original that a capture cannot supply.
        var result = await CaptureAsync(tenant, TransactionType.Reversal, 100m);

        Assert.Equal(TransactionLifecycleState.PendingReview, result.State);
        Assert.Null(result.FinancialTransactionId);
    }

    [SkippableFact]
    public async Task AnAmountBelowOnePesewaIsRejected()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var result = await CaptureAsync(tenant, TransactionType.CashIn, 0m);

        Assert.Equal(TransactionLifecycleState.Rejected, result.State);
        Assert.Null(result.FinancialTransactionId);
    }

    [SkippableFact]
    public async Task TheSameRealEventCapturedTwiceProducesOneTransaction()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var occurred = DateTimeOffset.UtcNow.AddMinutes(-10);

        var first = await CaptureAsync(tenant, TransactionType.CashIn, 250m, "REF-DUP", occurred);
        var second = await CaptureAsync(tenant, TransactionType.CashIn, 250m, "REF-DUP", occurred);

        Assert.Equal(TransactionLifecycleState.Accepted, first.State);
        Assert.True(second.IsDuplicate);
        Assert.Equal(TransactionLifecycleState.Rejected, second.State);

        await using var db = _postgres.CreateContext();

        // One ledger row, but both observations recorded — a duplicate arriving is auditable.
        Assert.Equal(1, await db.Transactions.CountAsync(x => x.OrganizationId == tenant.OrganizationId));
        Assert.Equal(2, await db.TransactionEvidence.CountAsync(x => x.OrganizationId == tenant.OrganizationId));

        var cash = await db.CashBalances.AsNoTracking().SingleAsync(x => x.BranchId == tenant.BranchId);
        Assert.Equal(250m, cash.CurrentCash);
    }

    [SkippableFact]
    public async Task TheSameEventInTwoTenantsProducesTwoTransactions()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        var occurred = DateTimeOffset.UtcNow.AddMinutes(-5);

        await CaptureAsync(alpha, TransactionType.CashIn, 80m, "REF-SHARED", occurred);
        await CaptureAsync(beta, TransactionType.CashIn, 80m, "REF-SHARED", occurred);

        await using var db = _postgres.CreateContext();

        // Fingerprints are organization-scoped: two tenants can legitimately have identical
        // transactions, and neither may suppress the other's.
        Assert.Equal(1, await db.Transactions.CountAsync(x => x.OrganizationId == alpha.OrganizationId));
        Assert.Equal(1, await db.Transactions.CountAsync(x => x.OrganizationId == beta.OrganizationId));
    }

    [SkippableFact]
    public async Task ManualCaptureUsesTheSameFingerprintAlgorithmAsEverySource()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var occurred = DateTimeOffset.UtcNow.AddMinutes(-3);
        var result = await CaptureAsync(tenant, TransactionType.CashIn, 42m, "REF-FP", occurred);

        var expected = EvidenceFingerprint.Compute(
            tenant.OrganizationId, "MTN", TransactionType.CashIn, 42m, "REF-FP", "0241234567", occurred);

        // One algorithm across every source. A manual entry describing the same event as a
        // captured SMS must collide with it, or the agent gets two ledger rows.
        Assert.Equal(expected, result.Fingerprint);
    }

    [SkippableFact]
    public async Task ManualCaptureIsAvailableOnEveryPlatform()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        await using var db = _postgres.CreateContext();
        var source = new ManualEntryEvidenceSource(
            db, new LedgerService(db), NullLogger<ManualEntryEvidenceSource>.Instance);

        // The guarantee that no platform is ever unable to record a transaction.
        foreach (var deviceType in Enum.GetValues<DeviceType>())
        {
            Assert.True(source.IsAvailableOn(deviceType));
        }

        Assert.Equal(EvidenceSourceType.ManualEntry, source.SourceType);
    }

    [SkippableFact]
    public async Task ImportedEvidenceIsADistinctSourceType()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        // A statement import is genuinely different from a person keying a figure in, and is
        // the only new source type this phase adds. iOS and Web manual capture reuse
        // ManualEntry rather than fragmenting the contract per platform.
        Assert.True(Enum.IsDefined(EvidenceSourceType.ImportedEvidence));
        Assert.NotEqual(EvidenceSourceType.ManualEntry, EvidenceSourceType.ImportedEvidence);

        var names = Enum.GetNames<EvidenceSourceType>();
        Assert.DoesNotContain(names, n => n.Contains("Ios", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Web", StringComparison.OrdinalIgnoreCase));

        await Task.CompletedTask;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<EvidenceCaptureResult> CaptureAsync(
        TenantSeed tenant,
        TransactionType type,
        decimal amount,
        string? reference = null,
        DateTimeOffset? occurredAt = null)
    {
        await using var db = _postgres.CreateContext();
        var source = new ManualEntryEvidenceSource(
            db, new LedgerService(db), NullLogger<ManualEntryEvidenceSource>.Instance);

        return await source.CaptureAsync(
            new EvidenceCapture(
                SourceType: EvidenceSourceType.ManualEntry,
                BranchId: tenant.BranchId,
                DeviceId: tenant.DeviceId,
                SessionId: null,
                Provider: "MTN",
                ObservedType: type,
                Amount: amount,
                OccurredAtUtc: occurredAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
                CustomerPhone: "0241234567",
                ProviderReference: reference ?? Guid.NewGuid().ToString("N")[..10]),
            tenant.OrganizationId,
            tenant.UserId);
    }

    private Task<TenantSeed> SeedAsync() => TenantSeedFactory.CreateAsync(_postgres);
}
