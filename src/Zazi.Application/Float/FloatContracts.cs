namespace Zazi.Application.Float;

/// <summary>What an owner hands an agent to work with.</summary>
/// <remarks>
/// Cash and float are given together in practice — an agent starting a shift takes notes for
/// the till and e-money for the wallet — so one record covers both. Either may be zero, and
/// either may be negative when the owner is taking money back at the end of a day.
/// </remarks>
public sealed record RecordFloatRequest(
    Guid AgentId,
    string Network,
    decimal CashAmount,
    decimal FloatAmount,
    string? Note = null);

/// <summary>What an agent is currently holding.</summary>
public sealed record AgentHoldingsDto(
    Guid AgentId,
    string AgentName,
    Guid? BranchId,
    string BranchName,
    decimal Cash,
    IReadOnlyList<NetworkFloatDto> Floats)
{
    /// <summary>Every network's float added together. Only meaningful as a rough total.</summary>
    public decimal TotalFloat => Floats.Sum(f => f.Amount);
}

public sealed record NetworkFloatDto(string Network, decimal Amount);

/// <summary>One movement in an agent's cash or float, most recent first.</summary>
public sealed record HoldingMovementDto(
    DateTimeOffset At,
    string Description,
    string Network,
    decimal CashDelta,
    decimal FloatDelta);

/// <summary>
/// Recording what an owner gives an agent, and reading back what they hold.
/// </summary>
/// <remarks>
/// Allocations are written as ordinary <c>Adjustment</c> transactions rather than into a table
/// of their own. That is deliberate: they then move balances through the same ledger every
/// other movement uses, appear in the same history, carry the same audit trail, and reverse the
/// same way. A parallel store would be a second source of truth about money, which is the thing
/// this system exists to avoid.
/// </remarks>
public interface IFloatService
{
    /// <summary>Records cash and/or float handed to an agent.</summary>
    Task RecordFloatAsync(
        RecordFloatRequest request,
        Guid organizationId,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>What every agent in the organization is currently holding.</summary>
    Task<IReadOnlyList<AgentHoldingsDto>> GetHoldingsAsync(
        Guid organizationId,
        Guid? branchId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Recent movements for one agent.</summary>
    Task<IReadOnlyList<HoldingMovementDto>> GetMovementsAsync(
        Guid organizationId,
        Guid agentId,
        int limit = 50,
        CancellationToken cancellationToken = default);
}
