using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Domain;

namespace Zazi.Infrastructure.Services;

/// <summary>
/// Compares a session's expected position against the counted position.
/// </summary>
/// <remarks>
/// <para>
/// Reconciliation is strictly <b>read-only</b> with respect to historical transactions. It
/// never edits an amount, a type, or a ledger delta to make a balance agree. A difference is
/// recorded as a discrepancy; correcting it requires an explicit
/// <see cref="TransactionType.Adjustment"/> raised through an authorised workflow, which
/// leaves both the original and the correction visible.
/// </para>
/// <para>
/// Expected cash is derived by summing the stored <c>CashDelta</c> values that
/// <see cref="LedgerPolicy"/> resolved at acceptance. Direction is never re-derived here,
/// which is what previously let this service disagree with the ledger.
/// </para>
/// </remarks>
public class ReconciliationService : IReconciliationService
{
    /// <summary>Differences at or above this magnitude demand a written explanation.</summary>
    public const decimal ExplanationRequiredThreshold = 1.00m;

    private readonly ApplicationDbContext _dbContext;

    public ReconciliationService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ReconciliationResultDto> ReconcileSessionAsync(
        Guid sessionId,
        decimal actualCash,
        decimal actualFloat,
        CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.Sessions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken)
            ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");

        var windowEnd = session.ClosedAt ?? DateTimeOffset.UtcNow;

        // Only states that affect the ledger are counted. Evidence that was rejected or is
        // pending review must never move an expected balance.
        var postable = await _dbContext.Transactions
            .AsNoTracking()
            .Where(x => x.OrganizationId == session.OrganizationId && x.BranchId == session.BranchId)
            .Where(x => x.TransactionAtUtc >= session.OpenedAt && x.TransactionAtUtc <= windowEnd)
            .Where(x => x.State == TransactionLifecycleState.Accepted
                || x.State == TransactionLifecycleState.Synced
                || x.State == TransactionLifecycleState.Reversed
                || x.State == TransactionLifecycleState.Adjusted)
            .Select(x => new { x.CashDelta, x.FloatDelta })
            .ToListAsync(cancellationToken);

        var cashInflow = postable.Where(x => x.CashDelta > 0m).Sum(x => x.CashDelta);
        var cashOutflow = -postable.Where(x => x.CashDelta < 0m).Sum(x => x.CashDelta);
        var netCashMovement = postable.Sum(x => x.CashDelta);
        var netFloatMovement = postable.Sum(x => x.FloatDelta);

        var expectedCash = LedgerPolicy.RoundToCurrency(session.OpeningCash + netCashMovement);
        var expectedFloat = LedgerPolicy.RoundToCurrency(session.OpeningFloat + netFloatMovement);

        var cashDifference = LedgerPolicy.RoundToCurrency(actualCash - expectedCash);
        var floatDifference = LedgerPolicy.RoundToCurrency(actualFloat - expectedFloat);

        var status = ResolveStatus(cashDifference, floatDifference);

        var record = new ReconciliationRecord
        {
            OrganizationId = session.OrganizationId,
            BranchId = session.BranchId,
            SessionId = session.Id,
            OpeningCash = session.OpeningCash,
            CashInflow = cashInflow,
            CashOutflow = cashOutflow,
            ExpectedCash = expectedCash,
            ActualCash = actualCash,
            Difference = cashDifference,
            Status = status
        };

        _dbContext.Reconciliations.Add(record);

        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = session.OrganizationId,
            UserId = session.UserId,
            DeviceId = session.DeviceId,
            Action = "RECONCILIATION_COMPLETED",
            Details =
                $"Session {session.Id}: expected cash {expectedCash:0.00}, counted {actualCash:0.00}, " +
                $"difference {cashDifference:+0.00;-0.00;0.00}; expected float {expectedFloat:0.00}, " +
                $"counted {actualFloat:0.00}, difference {floatDifference:+0.00;-0.00;0.00}. Status {status}. " +
                "No historical transaction was modified.",
            ActorType = "System"
        });

        if (status != "Balanced")
        {
            _dbContext.Alerts.Add(new AlertRecord
            {
                OrganizationId = session.OrganizationId,
                BranchId = session.BranchId,
                Type = "RECONCILIATION_DIFFERENCE",
                Message =
                    $"Session {session.Id} reconciled {status}: cash {cashDifference:+0.00;-0.00;0.00}, " +
                    $"float {floatDifference:+0.00;-0.00;0.00}.",
                Severity = Math.Abs(cashDifference) >= ExplanationRequiredThreshold ? "High" : "Medium",
                Network = string.Empty
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new ReconciliationResultDto(
            record.Id,
            record.OrganizationId,
            record.BranchId,
            record.SessionId,
            record.OpeningCash,
            record.CashInflow,
            record.CashOutflow,
            record.ExpectedCash,
            record.ActualCash,
            record.Difference,
            record.Status);
    }

    private static string ResolveStatus(decimal cashDifference, decimal floatDifference)
    {
        if (cashDifference == 0m && floatDifference == 0m)
        {
            return "Balanced";
        }

        // A discrepancy on both sides at once suggests a classification error rather than a
        // simple counting error, so it is escalated rather than labelled short or over.
        if (cashDifference != 0m && floatDifference != 0m)
        {
            return "RequiresReview";
        }

        var difference = cashDifference != 0m ? cashDifference : floatDifference;
        return difference < 0m ? "Short" : "Over";
    }
}
