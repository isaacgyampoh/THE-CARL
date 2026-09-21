using Microsoft.EntityFrameworkCore;
using Zazi.Application.Statements;
using Zazi.Domain;

namespace Zazi.Infrastructure.Statements;

/// <summary>
/// Builds statements from the ledger — for a day, a week, a month, a year or any range.
/// </summary>
/// <remarks>
/// <para>
/// Read straight from the transactions table, oldest first, the order a statement is read in.
/// Nothing here computes a balance: every figure is a sum of amounts or of the deltas the
/// ledger already recorded, so a statement cannot disagree with what actually moved.
/// </para>
/// <para>
/// Every line carries the customer's number and the exact time, because the use that
/// matters most is the customer who comes back to say "I came at 11:50 and withdrew fifty
/// cedis" — and a statement is what the agent or the owner hands over to settle it.
/// </para>
/// </remarks>
public sealed class StatementService : IStatementService
{
    /// <summary>
    /// The most lines one statement will carry.
    /// </summary>
    /// <remarks>
    /// A busy agent does a few hundred transactions a day, so a year for one agent fits with
    /// room to spare. An estate-wide year may not; when the cap is reached the statement says
    /// so in its header rather than presenting a partial list as the whole.
    /// </remarks>
    public const int MaximumLines = 50_000;

    private readonly ApplicationDbContext _dbContext;
    private readonly TimeProvider _clock;

    public StatementService(ApplicationDbContext dbContext, TimeProvider? clock = null)
    {
        _dbContext = dbContext;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<Statement> BuildAsync(StatementRequest request, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var (from, to, periodLabel) = StatementPeriods.Resolve(request.Period, request.From, request.To, now);

        var query = _dbContext.Transactions
            .AsNoTracking()
            .Where(t => t.OrganizationId == request.OrganizationId
                && t.TransactionAtUtc >= from
                && t.TransactionAtUtc < to);

        if (request.BranchId is { } branchId)
        {
            query = query.Where(t => t.BranchId == branchId);
        }

        if (request.AgentId is { } agentId)
        {
            query = query.Where(t => t.AgentId == agentId);
        }

        if (GhanaPhoneNumber.Normalise(request.CustomerPhone) is { } customer)
        {
            var lastNine = customer[1..];
            query = query.Where(t => t.CustomerPhoneNumber != null
                && (t.CustomerPhoneNumber == customer || t.CustomerPhoneNumber.EndsWith(lastNine)));
        }

        // One more than the cap, to know whether the cap was reached without counting the lot.
        var rows = await query
            .OrderBy(t => t.TransactionAtUtc)
            .ThenBy(t => t.Id)
            .Take(MaximumLines + 1)
            .Select(t => new
            {
                t.TransactionAtUtc,
                t.AgentId,
                t.Type,
                t.Network,
                t.CustomerPhoneNumber,
                t.ProviderReference,
                t.Amount,
                t.CashDelta,
                t.FloatDelta
            })
            .ToListAsync(cancellationToken);

        var truncated = rows.Count > MaximumLines;
        if (truncated)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var agentIds = rows.Select(r => r.AgentId).Distinct().ToList();
        if (request.AgentId is { } requested && !agentIds.Contains(requested))
        {
            agentIds.Add(requested);
        }

        var names = await _dbContext.Users
            .AsNoTracking()
            .Where(u => agentIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        var lines = rows
            .Select(r => new StatementLine(
                r.TransactionAtUtc,
                names.GetValueOrDefault(r.AgentId, "Unknown"),
                r.Type,
                r.Network,
                r.CustomerPhoneNumber,
                r.ProviderReference,
                r.Amount,
                r.CashDelta,
                r.FloatDelta))
            .ToList();

        var business = await _dbContext.Organizations
            .AsNoTracking()
            .Where(o => o.Id == request.OrganizationId)
            .Select(o => o.Name)
            .SingleOrDefaultAsync(cancellationToken) ?? "Zazi";

        var scope = await ScopeLabelAsync(request, names, cancellationToken);

        return new Statement(
            business,
            scope,
            periodLabel,
            from,
            to,
            now,
            Totals(lines),
            lines,
            truncated);
    }

    public async Task<StatementFile> RenderAsync(
        StatementRequest request,
        StatementFormat format,
        CancellationToken cancellationToken = default)
    {
        var statement = await BuildAsync(request, cancellationToken);
        var stem = FileStem(statement);

        return format switch
        {
            StatementFormat.Csv => new StatementFile($"{stem}.csv", "text/csv; charset=utf-8", CsvStatementWriter.Write(statement)),
            _ => new StatementFile($"{stem}.pdf", "application/pdf", PdfStatementWriter.Write(statement))
        };
    }

    /// <summary>The figures at the top: what came in, what went out, what was earned.</summary>
    internal static StatementTotals Totals(IReadOnlyList<StatementLine> lines)
    {
        var deposits = lines.Where(l => l.Type == TransactionType.CashIn).ToList();
        var withdrawals = lines.Where(l => l.Type == TransactionType.CashOut).ToList();

        return new StatementTotals(
            lines.Count,
            deposits.Count,
            deposits.Sum(l => l.Amount),
            withdrawals.Count,
            withdrawals.Sum(l => l.Amount),
            lines.Where(l => l.Type == TransactionType.Commission).Sum(l => l.Amount),
            lines.Sum(l => l.CashDelta),
            lines.Sum(l => l.FloatDelta));
    }

    private async Task<string> ScopeLabelAsync(
        StatementRequest request,
        IReadOnlyDictionary<Guid, string> names,
        CancellationToken cancellationToken)
    {
        var parts = new List<string>();

        if (request.AgentId is { } agentId)
        {
            parts.Add(names.GetValueOrDefault(agentId, "One agent"));
        }
        else
        {
            parts.Add("All agents");
        }

        if (request.BranchId is { } branchId)
        {
            var branch = await _dbContext.Branches.AsNoTracking()
                .Where(b => b.Id == branchId)
                .Select(b => b.Name)
                .SingleOrDefaultAsync(cancellationToken);
            if (branch is not null)
            {
                parts.Add(branch);
            }
        }

        if (GhanaPhoneNumber.Normalise(request.CustomerPhone) is { } customer)
        {
            parts.Add("customer " + GhanaPhoneNumber.Display(customer));
        }

        return string.Join(" · ", parts);
    }

    private static string FileStem(Statement statement)
    {
        static string Slug(string value)
        {
            var kept = new string(value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());
            while (kept.Contains("--", StringComparison.Ordinal))
            {
                kept = kept.Replace("--", "-", StringComparison.Ordinal);
            }

            return kept.Trim('-');
        }

        var lastDay = statement.ToUtc.AddDays(-1);
        var range = statement.FromUtc.Date == lastDay.Date
            ? $"{statement.FromUtc:yyyy-MM-dd}"
            : $"{statement.FromUtc:yyyy-MM-dd}-to-{lastDay:yyyy-MM-dd}";

        return $"statement-{Slug(statement.BusinessName)}-{range}";
    }
}
