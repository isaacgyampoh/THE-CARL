using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Application.Float;
using Zazi.Domain;


namespace Zazi.Infrastructure.Float;

/// <inheritdoc />
public sealed class FloatService : IFloatService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ITransactionService _transactions;

    public FloatService(ApplicationDbContext dbContext, ITransactionService transactions)
    {
        _dbContext = dbContext;
        _transactions = transactions;
    }

    public async Task RecordFloatAsync(
        RecordFloatRequest request,
        Guid organizationId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.CashAmount == 0m && request.FloatAmount == 0m)
        {
            throw new ArgumentException(
                "Enter an amount of cash, float, or both.", nameof(request));
        }

        var agent = await _dbContext.Users
            .SingleOrDefaultAsync(
                u => u.Id == request.AgentId && u.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new KeyNotFoundException("That person is not in this business.");

        // Balances hang off a branch as well as an agent. An owner is deliberately created
        // without one — they are business-wide, not branch-staff — and in a new business the
        // owner is usually the only person there is, so refusing them was refusing the whole
        // feature to every business on its first day. Where the person has no branch of their
        // own, the money is filed where they last traded, or in the business's first branch.
        var branchId = agent.BranchId
            ?? await _dbContext.Transactions.AsNoTracking()
                .Where(t => t.OrganizationId == organizationId && t.AgentId == request.AgentId)
                .OrderByDescending(t => t.TransactionAtUtc)
                .Select(t => (Guid?)t.BranchId)
                .FirstOrDefaultAsync(cancellationToken)
            ?? await _dbContext.Branches.AsNoTracking()
                .Where(b => b.OrganizationId == organizationId)
                .OrderBy(b => b.CreatedAt)
                .Select(b => (Guid?)b.Id)
                .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ArgumentException(
                "This business has no branch yet, so there is nowhere to record the money. Add one on the Team page.",
                nameof(request));

        var network = string.IsNullOrWhiteSpace(request.Network)
            ? "UNKNOWN"
            : request.Network.Trim().ToUpperInvariant();

        // An Adjustment, not a bespoke record: it moves the balances through the ledger every
        // other movement uses, and inherits its audit trail and reversal behaviour.
        //
        // Amount is the magnitude for reporting; the deltas carry the direction, which for an
        // allocation only the owner knows.
        await _transactions.CreateTransactionAsync(
            new CreateTransactionRequest(
                organizationId,
                branchId,
                request.AgentId,
                DeviceId: null,
                network,
                TransactionType.Adjustment,
                Math.Abs(request.CashAmount) + Math.Abs(request.FloatAmount),
                Money.DefaultCurrency,
                CustomerPhoneNumber: null,
                ProviderReference: null,
                TransactionSource.Manual,
                Notes: string.IsNullOrWhiteSpace(request.Note)
                    ? $"Float recorded by owner for {agent.FullName}."
                    : request.Note.Trim(),
                AdjustmentCashDelta: request.CashAmount,
                AdjustmentFloatDelta: request.FloatAmount,
                // A repeat of the same submission lands on the same identity, and the ledger's
                // uniqueness constraint keeps one record rather than two.
                ClientTransactionId: string.IsNullOrWhiteSpace(request.SubmissionToken)
                    ? null
                    : ClientTransactionId.Deterministic("portal-allocation", request.SubmissionToken.Trim())),
            cancellationToken);

        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = organizationId,
            UserId = actorUserId,
            Action = "FLOAT_RECORDED",
            Details =
                $"Cash {request.CashAmount:+0.00;-0.00;0.00}, {network} float "
                + $"{request.FloatAmount:+0.00;-0.00;0.00} recorded for {agent.FullName}.",
            ActorType = "User"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentHoldingsDto>> GetHoldingsAsync(
        Guid organizationId,
        Guid? branchId = null,
        CancellationToken cancellationToken = default)
    {
        var agents = await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.OrganizationId == organizationId
                && (branchId == null || u.BranchId == branchId))
            .Select(u => new { u.Id, u.FullName, u.BranchId })
            .ToListAsync(cancellationToken);

        var branchNames = await _dbContext.Branches
            .AsNoTracking()
            .Where(b => b.OrganizationId == organizationId)
            .ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);

        var cash = await _dbContext.CashBalances
            .AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.AgentId != null)
            .ToDictionaryAsync(c => c.AgentId!.Value, c => c.CurrentCash, cancellationToken);

        var floats = await _dbContext.FloatBalances
            .AsNoTracking()
            .Where(f => f.OrganizationId == organizationId && f.AgentId != null)
            .Select(f => new { AgentId = f.AgentId!.Value, f.Network, f.CurrentFloat })
            .ToListAsync(cancellationToken);

        return agents
            .Select(a => new AgentHoldingsDto(
                a.Id,
                a.FullName,
                a.BranchId,
                a.BranchId is { } id && branchNames.TryGetValue(id, out var name) ? name : "—",
                cash.TryGetValue(a.Id, out var held) ? held : 0m,
                floats
                    .Where(f => f.AgentId == a.Id)
                    .OrderBy(f => f.Network, StringComparer.Ordinal)
                    .Select(f => new NetworkFloatDto(f.Network, f.CurrentFloat))
                    .ToArray()))
            .OrderBy(a => a.AgentName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<AgentLedgerDto?> GetAgentLedgerAsync(
        Guid organizationId,
        Guid agentId,
        int historyLimit = 50,
        CancellationToken cancellationToken = default)
    {
        var agent = await _dbContext.Users.AsNoTracking()
            .Where(u => u.Id == agentId && u.OrganizationId == organizationId)
            .Select(u => new { u.Id, u.FullName, u.BranchId })
            .SingleOrDefaultAsync(cancellationToken);
        if (agent is null)
        {
            return null;
        }

        var branchName = agent.BranchId is { } branchId
            ? await _dbContext.Branches.AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Name)
                .FirstOrDefaultAsync(cancellationToken) ?? "—"
            : "—";

        var cash = await _dbContext.CashBalances.AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.AgentId == agentId)
            .SumAsync(c => (decimal?)c.CurrentCash, cancellationToken) ?? 0m;

        var floats = await _dbContext.FloatBalances.AsNoTracking()
            .Where(f => f.OrganizationId == organizationId && f.AgentId == agentId)
            .Select(f => new { f.Network, f.CurrentFloat })
            .ToListAsync(cancellationToken);

        // Ghana keeps GMT all year, so the UTC day is the business day.
        var dayStart = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);

        var today = await _dbContext.Transactions.AsNoTracking()
            .Where(t => t.OrganizationId == organizationId && t.AgentId == agentId
                && t.TransactionAtUtc >= dayStart
                && (t.CashDelta != 0m || t.FloatDelta != 0m))
            .Select(t => new { t.Type, t.CashDelta, t.FloatDelta })
            .ToListAsync(cancellationToken);

        var allocations = today.Where(t => t.Type == TransactionType.Adjustment).ToList();
        var trading = today.Where(t => t.Type != TransactionType.Adjustment).ToList();
        var movement = new AgentDayMovement(
            CashAllocated: allocations.Where(t => t.CashDelta > 0).Sum(t => t.CashDelta),
            FloatAllocated: allocations.Where(t => t.FloatDelta > 0).Sum(t => t.FloatDelta),
            CashIn: trading.Where(t => t.CashDelta > 0).Sum(t => t.CashDelta),
            CashOut: -trading.Where(t => t.CashDelta < 0).Sum(t => t.CashDelta),
            FloatIn: trading.Where(t => t.FloatDelta > 0).Sum(t => t.FloatDelta),
            FloatOut: -trading.Where(t => t.FloatDelta < 0).Sum(t => t.FloatDelta),
            // Money taken back at the end of a shift is an allocation in reverse.
            CashAdjusted: allocations.Where(t => t.CashDelta < 0).Sum(t => t.CashDelta),
            FloatAdjusted: allocations.Where(t => t.FloatDelta < 0).Sum(t => t.FloatDelta),
            Transactions: trading.Count);

        var movements = await GetMovementsAsync(organizationId, agentId, historyLimit, cancellationToken);

        // Running balances are wound backwards from what is held now, so each line shows where
        // the balance stood after it — and the newest line always agrees with the headline.
        var runningCash = cash;
        var runningFloat = floats.Sum(f => f.CurrentFloat);
        var history = new List<AgentLedgerEntry>(movements.Count);
        foreach (var m in movements)
        {
            history.Add(new AgentLedgerEntry(
                m.At, m.Description, m.Network, m.CashDelta, m.FloatDelta, runningCash, runningFloat,
                IsAllocation: !TradingDescriptions.Contains(m.Description)));
            runningCash -= m.CashDelta;
            runningFloat -= m.FloatDelta;
        }

        return new AgentLedgerDto(
            agent.Id,
            agent.FullName,
            branchName,
            cash,
            floats.OrderBy(f => f.Network, StringComparer.Ordinal)
                .Select(f => new NetworkFloatDto(f.Network, f.CurrentFloat)).ToArray(),
            OpeningCash: cash - movement.NetCash,
            OpeningFloat: floats.Sum(f => f.CurrentFloat) - movement.NetFloat,
            movement,
            history);
    }

    /// <summary>The descriptions GetMovementsAsync gives a traded transaction; anything else is an allocation note.</summary>
    private static readonly HashSet<string> TradingDescriptions =
        Enum.GetNames<TransactionType>().Where(n => n != nameof(TransactionType.Adjustment)).ToHashSet(StringComparer.Ordinal);

    public async Task<IReadOnlyList<HoldingMovementDto>> GetMovementsAsync(
        Guid organizationId,
        Guid agentId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        // Only what actually moved a balance. A held or rejected transaction is real history
        // but did not change what the agent is carrying, and showing it in a running balance
        // invites exactly the reconciliation argument this view exists to settle.
        var rows = await _dbContext.Transactions
            .AsNoTracking()
            .Where(t => t.OrganizationId == organizationId
                && t.AgentId == agentId
                && (t.CashDelta != 0m || t.FloatDelta != 0m))
            .OrderByDescending(t => t.TransactionAtUtc)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(t => new
            {
                t.TransactionAtUtc,
                t.Type,
                t.Notes,
                t.Network,
                t.CashDelta,
                t.FloatDelta
            })
            .ToListAsync(cancellationToken);

        // Shaped after the query rather than inside it: an owner's own note is the useful
        // label for an allocation, and choosing between it and the type name is not something
        // to ask the database to translate.
        return rows
            .Select(t => new HoldingMovementDto(
                t.TransactionAtUtc,
                t.Type == TransactionType.Adjustment ? (t.Notes ?? "Adjustment") : t.Type.ToString(),
                t.Network,
                t.CashDelta,
                t.FloatDelta))
            .ToArray();
    }
}
