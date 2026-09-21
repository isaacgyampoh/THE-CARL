using Microsoft.EntityFrameworkCore;
using Zazi.Application.Closing;
using Zazi.Domain;

namespace Zazi.Infrastructure.Services;

/// <summary>
/// Records an agent's end-of-day count and says whether it agrees with the ledger.
/// </summary>
/// <remarks>
/// Read-only towards transactions, like reconciliation: a difference is reported and alerted,
/// never "fixed" by changing what was recorded. The movement since the last close is summed
/// from the stored deltas, so direction is never decided twice.
/// </remarks>
public sealed class DayCloseService : IDayCloseService
{
    private readonly ApplicationDbContext _db;
    private readonly TimeProvider _clock;

    public DayCloseService(ApplicationDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<DayCloseResult> CloseAsync(DayCloseRequest request, CancellationToken cancellationToken = default)
    {
        if (request.CountedCash < 0m || request.CountedFloat < 0m)
        {
            throw new DayCloseRejectedException("Counted cash and float cannot be negative.");
        }

        if (request.CountedCash > DayCloseRules.MaximumFigure || request.CountedFloat > DayCloseRules.MaximumFigure)
        {
            throw new DayCloseRejectedException("That figure is too large. Check it and try again.");
        }

        var now = _clock.GetUtcNow();

        var previous = await _db.DayCloses.AsNoTracking()
            .Where(c => c.OrganizationId == request.OrganizationId && c.AgentId == request.AgentId)
            .OrderByDescending(c => c.ClosedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        // Everything recorded for this agent since the last count, by when it happened. A
        // transaction done before the count but synced after it is missing from this close —
        // the count already includes it, so it shows here as a difference and not at the next.
        var since = previous?.ClosedAtUtc ?? DateTimeOffset.MinValue;
        var recorded = _db.Transactions.AsNoTracking()
            .Where(t => t.OrganizationId == request.OrganizationId
                && t.AgentId == request.AgentId
                && t.TransactionAtUtc > since
                && t.TransactionAtUtc <= now
                && (t.State == TransactionLifecycleState.Accepted
                    || t.State == TransactionLifecycleState.Synced
                    || t.State == TransactionLifecycleState.Reversed
                    || t.State == TransactionLifecycleState.Adjusted));
        var cashMovement = await recorded.SumAsync(t => t.CashDelta, cancellationToken);
        var floatMovement = await recorded.SumAsync(t => t.FloatDelta, cancellationToken);
        var count = await recorded.CountAsync(cancellationToken);

        // A caller without a branch on their token (an owner closing for themselves) is filed
        // under their own branch, or wherever they last traded.
        var branchId = request.BranchId != Guid.Empty
            ? request.BranchId
            : await _db.Users.AsNoTracking().Where(u => u.Id == request.AgentId).Select(u => u.BranchId).FirstOrDefaultAsync(cancellationToken)
              ?? await _db.Transactions.AsNoTracking()
                  .Where(t => t.OrganizationId == request.OrganizationId && t.AgentId == request.AgentId)
                  .OrderByDescending(t => t.TransactionAtUtc)
                  .Select(t => (Guid?)t.BranchId)
                  .FirstOrDefaultAsync(cancellationToken)
              ?? Guid.Empty;

        var close = new DayClose
        {
            OrganizationId = request.OrganizationId,
            BranchId = branchId,
            AgentId = request.AgentId,
            ClosedAtUtc = now,
            BusinessDate = DateOnly.FromDateTime(now.UtcDateTime),
            Channel = request.Channel,
            CountedCash = LedgerPolicy.RoundToCurrency(request.CountedCash),
            CountedFloat = LedgerPolicy.RoundToCurrency(request.CountedFloat),
            CashMovement = cashMovement,
            FloatMovement = floatMovement,
            TransactionCount = count,
            PreviousCloseId = previous?.Id,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()[..Math.Min(request.Note.Trim().Length, 500)]
        };

        if (previous is not null)
        {
            close.ExpectedCash = LedgerPolicy.RoundToCurrency(previous.CountedCash + close.CashMovement);
            close.ExpectedFloat = LedgerPolicy.RoundToCurrency(previous.CountedFloat + close.FloatMovement);
        }

        _db.DayCloses.Add(close);

        var agentName = await AgentNameAsync(request.AgentId, cancellationToken);
        var result = ToResult(close, agentName);

        _db.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = close.OrganizationId,
            UserId = close.AgentId,
            Action = "DAY_CLOSED",
            Details = result.IsBaseline
                ? $"First close by {close.Channel}: cash {close.CountedCash:0.00}, float {close.CountedFloat:0.00}. Baseline set."
                : $"Close by {close.Channel}: cash counted {close.CountedCash:0.00} vs expected {close.ExpectedCash:0.00}, " +
                  $"float counted {close.CountedFloat:0.00} vs expected {close.ExpectedFloat:0.00}. {result.Status}.",
            ActorType = "User"
        });

        if (result.Status == "Short" || result.Status == "Over")
        {
            _db.Alerts.Add(new AlertRecord
            {
                OrganizationId = close.OrganizationId,
                BranchId = close.BranchId,
                Type = "DAY_CLOSE_DIFFERENCE",
                Message = $"{agentName ?? "An agent"} closed {result.Status.ToLowerInvariant()}: " +
                          $"cash {result.CashDifference:+0.00;-0.00;0.00}, float {result.FloatDifference:+0.00;-0.00;0.00}.",
                Severity = result.Status == "Short" ? "High" : "Medium",
                Network = string.Empty
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<DayCloseResult?> LatestAsync(Guid organizationId, Guid agentId, CancellationToken cancellationToken = default)
    {
        var latest = await _db.DayCloses.AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.AgentId == agentId)
            .OrderByDescending(c => c.ClosedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return latest is null ? null : ToResult(latest, await AgentNameAsync(agentId, cancellationToken));
    }

    public async Task<IReadOnlyList<DayCloseResult>> ForDayAsync(
        Guid organizationId, Guid? branchId, DateOnly businessDate, CancellationToken cancellationToken = default)
    {
        var closes = await _db.DayCloses.AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.BusinessDate == businessDate)
            .Where(c => branchId == null || c.BranchId == branchId)
            .ToListAsync(cancellationToken);

        // The latest count per agent stands; earlier ones stay in the audit trail.
        var standing = closes
            .GroupBy(c => c.AgentId)
            .Select(g => g.OrderByDescending(c => c.ClosedAtUtc).First())
            .ToList();

        var agentIds = standing.Select(c => c.AgentId).ToList();
        var names = await _db.Users.AsNoTracking()
            .Where(u => agentIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        return standing
            .Select(c => ToResult(c, names.GetValueOrDefault(c.AgentId)))
            .OrderBy(r => r.Status switch { "Short" => 0, "Over" => 1, "Baseline" => 2, _ => 3 })
            .ThenBy(r => r.AgentName)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> NotClosedAsync(
        Guid organizationId, Guid? branchId, DateOnly businessDate, CancellationToken cancellationToken = default)
    {
        var dayStart = new DateTimeOffset(businessDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);

        var traded = _db.Transactions.AsNoTracking()
            .Where(t => t.OrganizationId == organizationId
                && t.TransactionAtUtc >= dayStart && t.TransactionAtUtc < dayEnd)
            .Where(t => branchId == null || t.BranchId == branchId)
            .Select(t => t.AgentId)
            .Distinct();

        // A close made after midnight still closes the day it counts: someone counting at
        // 00:20 is finishing yesterday, not starting today.
        var closed = _db.DayCloses.AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.ClosedAtUtc >= dayStart && c.ClosedAtUtc < dayEnd.AddHours(6))
            .Select(c => c.AgentId);

        return await _db.Users.AsNoTracking()
            .Where(u => traded.Contains(u.Id) && !closed.Contains(u.Id))
            .OrderBy(u => u.FullName)
            .Select(u => u.FullName)
            .ToListAsync(cancellationToken);
    }

    private async Task<string?> AgentNameAsync(Guid agentId, CancellationToken cancellationToken) =>
        await _db.Users.AsNoTracking()
            .Where(u => u.Id == agentId)
            .Select(u => u.FullName)
            .FirstOrDefaultAsync(cancellationToken);

    private static DayCloseResult ToResult(DayClose c, string? agentName) => new(
        c.Id,
        c.AgentId,
        agentName,
        c.ClosedAtUtc,
        c.BusinessDate,
        c.Channel,
        c.CountedCash,
        c.CountedFloat,
        c.ExpectedCash,
        c.ExpectedFloat,
        c.CashDifference,
        c.FloatDifference,
        c.TransactionCount,
        IsBaseline: c.ExpectedCash is null,
        c.Note);
}
