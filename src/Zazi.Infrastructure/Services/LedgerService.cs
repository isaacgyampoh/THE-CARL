using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Domain;

namespace Zazi.Infrastructure.Services;

/// <summary>
/// Projects accepted financial transactions onto branch cash and float balances.
/// </summary>
/// <remarks>
/// <para>
/// This service applies movements; it never decides them. Direction comes from
/// <see cref="LedgerPolicy"/> at acceptance time and is stored on the transaction, so a
/// projection rebuilt later cannot drift from the original posting.
/// </para>
/// <para>
/// <b>Concurrency.</b> On PostgreSQL the projection is a single atomic
/// <c>INSERT … ON CONFLICT … DO UPDATE</c> that increments in the database. A
/// read-modify-write from application memory would lose updates under concurrent posting —
/// two requests both read 100, both write 101, and one transaction's money vanishes — and
/// would additionally collide on the balance row's unique index the first time two
/// transactions for a new branch arrived together.
/// </para>
/// </remarks>
public class LedgerService : ILedgerService
{
    private readonly ApplicationDbContext _dbContext;

    public LedgerService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<BranchLedgerSnapshotDto> GetBranchLedgerAsync(
        Guid organizationId,
        Guid branchId,
        string network,
        CancellationToken cancellationToken = default)
    {
        var cashBalance = await _dbContext.CashBalances
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.BranchId == branchId, cancellationToken);

        var floatBalance = await _dbContext.FloatBalances
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.BranchId == branchId && x.Network == network,
                cancellationToken);

        return new BranchLedgerSnapshotDto(
            organizationId,
            branchId,
            cashBalance?.CurrentCash ?? 0m,
            floatBalance?.CurrentFloat ?? 0m,
            string.IsNullOrWhiteSpace(network) ? "MTN" : network,
            floatBalance?.Threshold ?? 0m,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Applies a transaction's movement to the branch balances.
    /// </summary>
    /// <remarks>
    /// Does not commit. The caller owns the database transaction so that evidence, the ledger
    /// row, the balance projection and the audit entry commit together or not at all. On
    /// PostgreSQL the increments are executed immediately on the ambient transaction; the
    /// caller's <c>SaveChanges</c> completes the rest of the unit of work.
    /// </remarks>
    public async Task ApplyTransactionAsync(
        FinancialTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (!transaction.State.AffectsLedger())
        {
            // Evidence that was never accepted, or was rejected, has no balance effect.
            return;
        }

        if (transaction.CashDelta == 0m && transaction.FloatDelta == 0m)
        {
            return;
        }

        if (_dbContext.Database.IsRelational())
        {
            await ApplyAtomicallyAsync(transaction, cancellationToken);
            return;
        }

        await ApplyInMemoryAsync(transaction, cancellationToken);
    }

    /// <summary>
    /// Atomic upsert. Concurrent callers serialise on the unique index instead of racing,
    /// and the increment happens in the database rather than in application memory.
    /// </summary>
    private async Task ApplyAtomicallyAsync(FinancialTransaction transaction, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "CashBalances" ("Id", "OrganizationId", "BranchId", "AgentId", "OpeningCash", "CurrentCash", "CreatedAt", "UpdatedAt")
             VALUES ({Guid.NewGuid()}, {transaction.OrganizationId}, {transaction.BranchId}, {transaction.AgentId}, 0, {transaction.CashDelta}, {now}, {now})
             ON CONFLICT ("OrganizationId", "BranchId", "AgentId")
             DO UPDATE SET "CurrentCash" = "CashBalances"."CurrentCash" + {transaction.CashDelta},
                           "UpdatedAt" = {now};
             """,
            cancellationToken);

        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "FloatBalances" ("Id", "OrganizationId", "BranchId", "AgentId", "Network", "OpeningFloat", "CurrentFloat", "Threshold", "CreatedAt", "UpdatedAt")
             VALUES ({Guid.NewGuid()}, {transaction.OrganizationId}, {transaction.BranchId}, {transaction.AgentId}, {transaction.Network}, 0, {transaction.FloatDelta}, 0, {now}, {now})
             ON CONFLICT ("OrganizationId", "BranchId", "AgentId", "Network")
             DO UPDATE SET "CurrentFloat" = "FloatBalances"."CurrentFloat" + {transaction.FloatDelta},
                           "UpdatedAt" = {now};
             """,
            cancellationToken);
    }

    /// <summary>
    /// Fallback for the EF InMemory provider, which supports neither raw SQL nor unique
    /// indexes. Single-threaded unit tests only; never a production path.
    /// </summary>
    private async Task ApplyInMemoryAsync(FinancialTransaction transaction, CancellationToken cancellationToken)
    {
        var cashBalance = await _dbContext.CashBalances
            .SingleOrDefaultAsync(
                x => x.OrganizationId == transaction.OrganizationId
                    && x.BranchId == transaction.BranchId
                    && x.AgentId == transaction.AgentId,
                cancellationToken);

        if (cashBalance is null)
        {
            // Per agent, not per branch: two agents at one counter each hold their own cash,
            // and a pooled figure cannot answer how much the person in front of you is carrying.
            cashBalance = new CashBalance
            {
                OrganizationId = transaction.OrganizationId,
                BranchId = transaction.BranchId,
                AgentId = transaction.AgentId
            };
            _dbContext.CashBalances.Add(cashBalance);
        }

        var floatBalance = await _dbContext.FloatBalances
            .SingleOrDefaultAsync(
                x => x.OrganizationId == transaction.OrganizationId
                    && x.BranchId == transaction.BranchId
                    && x.AgentId == transaction.AgentId
                    && x.Network == transaction.Network,
                cancellationToken);

        if (floatBalance is null)
        {
            floatBalance = new FloatBalance
            {
                OrganizationId = transaction.OrganizationId,
                BranchId = transaction.BranchId,
                AgentId = transaction.AgentId,
                Network = transaction.Network
            };
            _dbContext.FloatBalances.Add(floatBalance);
        }

        cashBalance.CurrentCash += transaction.CashDelta;
        floatBalance.CurrentFloat += transaction.FloatDelta;
        cashBalance.UpdatedAt = DateTimeOffset.UtcNow;
        floatBalance.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
