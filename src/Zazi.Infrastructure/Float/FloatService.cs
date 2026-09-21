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

        if (agent.BranchId is not { } branchId)
        {
            // Balances hang off a branch as well as an agent, so someone with no branch has
            // nowhere for the money to land.
            throw new ArgumentException(
                $"{agent.FullName} is not assigned to a branch, so float cannot be recorded against them.",
                nameof(request));
        }

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
                AdjustmentFloatDelta: request.FloatAmount),
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
