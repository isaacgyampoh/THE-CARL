using Microsoft.EntityFrameworkCore;
using Zazi.Application.Growth;
using Zazi.Domain;

namespace Zazi.Infrastructure.Growth;

/// <summary>Daily trading figures for the owner's dashboard, from the ledger's stored rows.</summary>
public sealed class InsightsService : IInsightsService
{
    private readonly ApplicationDbContext _db;
    private readonly TimeProvider _clock;

    public InsightsService(ApplicationDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<DailyActivity>> DailyAsync(Guid organizationId, Guid? branchId, int days = 31,
        CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 92);
        // Ghana keeps GMT all year, so the UTC day is the business day.
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var first = today.AddDays(-(days - 1));
        var from = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        // Only the columns a chart needs, and only for what affects the ledger.
        var rows = await _db.Transactions.AsNoTracking()
            .Where(t => t.OrganizationId == organizationId && t.TransactionAtUtc >= from)
            .Where(t => branchId == null || t.BranchId == branchId)
            .Where(t => t.State == TransactionLifecycleState.Accepted
                || t.State == TransactionLifecycleState.Synced
                || t.State == TransactionLifecycleState.Reversed
                || t.State == TransactionLifecycleState.Adjusted)
            .Where(t => t.Type == TransactionType.CashIn || t.Type == TransactionType.CashOut || t.Type == TransactionType.Commission)
            .Select(t => new { t.TransactionAtUtc, t.Type, t.Amount })
            .ToListAsync(cancellationToken);

        var byDay = rows.GroupBy(r => DateOnly.FromDateTime(r.TransactionAtUtc.UtcDateTime))
            .ToDictionary(g => g.Key, g => g.ToList());

        var series = new List<DailyActivity>(days);
        for (var day = first; day <= today; day = day.AddDays(1))
        {
            var list = byDay.GetValueOrDefault(day) ?? [];
            series.Add(new DailyActivity(
                day,
                list.Count(r => r.Type != TransactionType.Commission),
                list.Where(r => r.Type == TransactionType.CashIn).Sum(r => r.Amount),
                list.Where(r => r.Type == TransactionType.CashOut).Sum(r => r.Amount),
                list.Where(r => r.Type == TransactionType.Commission).Sum(r => r.Amount)));
        }

        return series;
    }
}
