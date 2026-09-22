namespace Zazi.Application.Float;

/// <summary>What an owner hands an agent to work with.</summary>
/// <remarks>
/// Cash and float are given together in practice — an agent starting a shift takes notes for
/// the till and e-money for the wallet — so one record covers both. Either may be zero, and
/// either may be negative when the owner is taking money back at the end of a day.
/// </remarks>
/// <param name="SubmissionToken">
/// Identifies one attempt to record. Sent again when the same form is submitted twice — the
/// owner pressing "Record" once more because the page seemed to hang — so the ledger recognises
/// the repeat and keeps one record instead of two.
/// </param>
public sealed record RecordFloatRequest(
    Guid AgentId,
    string Network,
    decimal CashAmount,
    decimal FloatAmount,
    string? Note = null,
    string? SubmissionToken = null);

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

/// <summary>What moved through an agent's hands in one day.</summary>
public sealed record AgentDayMovement(
    decimal CashAllocated,
    decimal FloatAllocated,
    decimal CashIn,
    decimal CashOut,
    decimal FloatIn,
    decimal FloatOut,
    decimal CashAdjusted,
    decimal FloatAdjusted,
    int Transactions)
{
    public decimal NetCash => CashAllocated + CashIn - CashOut + CashAdjusted;
    public decimal NetFloat => FloatAllocated + FloatIn - FloatOut + FloatAdjusted;
}

/// <summary>
/// One line of an agent's ledger, with what the balances stood at after it.
/// </summary>
/// <remarks>
/// The running figures are computed backwards from today's balances, so every line explains
/// how the current balance came about rather than restating a stored number.
/// </remarks>
public sealed record AgentLedgerEntry(
    DateTimeOffset At,
    string Description,
    string Network,
    decimal CashDelta,
    decimal FloatDelta,
    decimal RunningCash,
    decimal RunningFloat,
    bool IsAllocation);

/// <summary>Everything the owner needs to answer "what is this agent holding, and why".</summary>
public sealed record AgentLedgerDto(
    Guid AgentId,
    string AgentName,
    string BranchName,
    decimal Cash,
    IReadOnlyList<NetworkFloatDto> Floats,
    decimal OpeningCash,
    decimal OpeningFloat,
    AgentDayMovement Today,
    IReadOnlyList<AgentLedgerEntry> History)
{
    public decimal TotalFloat => Floats.Sum(f => f.Amount);

    /// <summary>Cash plus float: the whole of what the business has in this agent's hands.</summary>
    public decimal Total => Cash + TotalFloat;
}

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

    /// <summary>
    /// One agent's position: what they hold now, what moved today, and the history that
    /// produced it — the answer to "how much does my agent have" without a phone call.
    /// </summary>
    Task<AgentLedgerDto?> GetAgentLedgerAsync(
        Guid organizationId,
        Guid agentId,
        int historyLimit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>Recent movements for one agent.</summary>
    Task<IReadOnlyList<HoldingMovementDto>> GetMovementsAsync(
        Guid organizationId,
        Guid agentId,
        int limit = 50,
        CancellationToken cancellationToken = default);
}
