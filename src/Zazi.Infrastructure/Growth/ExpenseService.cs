using Microsoft.EntityFrameworkCore;
using Zazi.Application.Growth;
using Zazi.Domain;

namespace Zazi.Infrastructure.Growth;

/// <summary>
/// What the business spends, and what it therefore keeps.
/// </summary>
/// <remarks>
/// Kept apart from the transaction ledger on purpose: an expense is not mobile money moving
/// between a customer and an agent, and putting it through the ledger would change balances
/// that must only reflect cash and float. It is joined to commission only when profit is read.
/// </remarks>
public sealed class ExpenseService : IExpenseService
{
    /// <summary>Above this and it is a typing slip, not a cost.</summary>
    private const decimal Maximum = 1_000_000m;

    private readonly ApplicationDbContext _db;
    private readonly TimeProvider _clock;

    public ExpenseService(ApplicationDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ExpenseDto> RecordAsync(Guid organizationId, Guid? branchId, Guid? agentId,
        ExpenseCategory category, decimal amount, DateOnly spentOn, string? note, Guid recordedByUserId,
        string? submissionToken, CancellationToken cancellationToken = default)
    {
        if (amount == 0m || Math.Abs(amount) > Maximum || decimal.Round(amount, 2) != amount)
        {
            throw new ExpenseRejectedException("Enter an amount in cedis and pesewas, above zero.");
        }

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        if (spentOn > today)
        {
            throw new ExpenseRejectedException("That date is in the future.");
        }

        if (spentOn < today.AddYears(-2))
        {
            throw new ExpenseRejectedException("That date is too long ago to record here.");
        }

        if (agentId is { } agent
            && !await _db.Users.AsNoTracking().AnyAsync(u => u.Id == agent && u.OrganizationId == organizationId, cancellationToken))
        {
            throw new ExpenseRejectedException("That person is not in this business.");
        }

        // A repeat of the same submission is already recorded; hand back what is there rather
        // than adding a second cost.
        if (!string.IsNullOrWhiteSpace(submissionToken))
        {
            var existing = await _db.Expenses.AsNoTracking()
                .FirstOrDefaultAsync(e => e.OrganizationId == organizationId && e.SubmissionToken == submissionToken,
                    cancellationToken);
            if (existing is not null)
            {
                return await ToDtoAsync(existing, cancellationToken);
            }
        }

        var expense = new BusinessExpense
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            AgentId = agentId,
            Category = category,
            Amount = decimal.Round(amount, 2),
            SpentOn = spentOn,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 500)],
            RecordedByUserId = recordedByUserId,
            SubmissionToken = string.IsNullOrWhiteSpace(submissionToken) ? null : submissionToken.Trim()
        };

        _db.Expenses.Add(expense);
        _db.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = organizationId,
            UserId = recordedByUserId,
            Action = "EXPENSE_RECORDED",
            Details = $"{category}: GHS {expense.Amount:0.00} on {spentOn:d MMM yyyy}.",
            ActorType = "User"
        });

        await _db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(expense, cancellationToken);
    }

    public async Task<IReadOnlyList<ExpenseDto>> ListAsync(Guid organizationId, Guid? branchId, DateOnly from,
        DateOnly to, CancellationToken cancellationToken = default)
    {
        var expenses = await Scoped(organizationId, branchId, from, to)
            .OrderByDescending(e => e.SpentOn).ThenByDescending(e => e.CreatedAt)
            .Take(500)
            .ToListAsync(cancellationToken);

        var names = await NamesAsync(expenses.SelectMany(e => new[] { e.AgentId, (Guid?)e.RecordedByUserId }), cancellationToken);
        return expenses
            .Select(e => new ExpenseDto(e.Id, e.SpentOn, e.Category, e.Amount, e.Note, e.AgentId,
                e.AgentId is { } id ? names.GetValueOrDefault(id) : null,
                names.GetValueOrDefault(e.RecordedByUserId) ?? "—"))
            .ToList();
    }

    public async Task<ProfitSummary> ProfitAsync(Guid organizationId, Guid? branchId, DateOnly from, DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var fromUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toUtc = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var commission = await _db.Transactions.AsNoTracking()
            .Where(t => t.OrganizationId == organizationId && t.Type == TransactionType.Commission
                && t.TransactionAtUtc >= fromUtc && t.TransactionAtUtc < toUtc)
            .Where(t => branchId == null || t.BranchId == branchId)
            .Where(t => t.State == TransactionLifecycleState.Accepted
                || t.State == TransactionLifecycleState.Synced
                || t.State == TransactionLifecycleState.Reversed
                || t.State == TransactionLifecycleState.Adjusted)
            .SumAsync(t => (decimal?)t.Amount, cancellationToken) ?? 0m;

        var spend = await Scoped(organizationId, branchId, from, to)
            .GroupBy(e => e.Category)
            .Select(g => new { Category = g.Key, Amount = g.Sum(e => e.Amount) })
            .ToListAsync(cancellationToken);

        return new ProfitSummary(
            from, to, commission, spend.Sum(s => s.Amount),
            spend.Where(s => s.Amount != 0m)
                .OrderByDescending(s => s.Amount)
                .Select(s => new CategorySpend(s.Category, s.Amount))
                .ToList());
    }

    private IQueryable<BusinessExpense> Scoped(Guid organizationId, Guid? branchId, DateOnly from, DateOnly to) =>
        _db.Expenses.AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.SpentOn >= from && e.SpentOn <= to)
            // An expense with no branch belongs to the whole business, so a branch's own view
            // shows its costs and not the rent for a shop it does not run.
            .Where(e => branchId == null || e.BranchId == branchId);

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var list = ids.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        return await _db.Users.AsNoTracking().Where(u => list.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
    }

    private async Task<ExpenseDto> ToDtoAsync(BusinessExpense expense, CancellationToken cancellationToken)
    {
        var names = await NamesAsync([expense.AgentId, expense.RecordedByUserId], cancellationToken);
        return new ExpenseDto(expense.Id, expense.SpentOn, expense.Category, expense.Amount, expense.Note,
            expense.AgentId, expense.AgentId is { } id ? names.GetValueOrDefault(id) : null,
            names.GetValueOrDefault(expense.RecordedByUserId) ?? "—");
    }
}
