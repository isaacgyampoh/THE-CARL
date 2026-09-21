namespace Zazi.Application.Closing;

public static class DayCloseChannels
{
    public const string App = "App";
    public const string Sms = "SMS";
    public const string Portal = "Portal";
}

/// <summary>
/// A count to record. Who is closing is decided by the caller from the signed-in identity or
/// the linked keypad phone, never taken from the request as given.
/// </summary>
public sealed record DayCloseRequest(
    Guid OrganizationId,
    Guid BranchId,
    Guid AgentId,
    decimal CountedCash,
    decimal CountedFloat,
    string Channel,
    string? Note = null);

/// <summary>Where the agent stands at a close.</summary>
public sealed record DayCloseResult(
    Guid Id,
    Guid AgentId,
    string? AgentName,
    DateTimeOffset ClosedAtUtc,
    DateOnly BusinessDate,
    string Channel,
    decimal CountedCash,
    decimal CountedFloat,
    decimal? ExpectedCash,
    decimal? ExpectedFloat,
    decimal? CashDifference,
    decimal? FloatDifference,
    int TransactionCount,
    bool IsBaseline,
    string? Note)
{
    /// <summary>Balanced, Over, Short — or Baseline for a first close.</summary>
    public string Status
    {
        get
        {
            if (IsBaseline)
            {
                return "Baseline";
            }

            var cash = CashDifference ?? 0m;
            var floatDiff = FloatDifference ?? 0m;
            if (Math.Abs(cash) < DayCloseRules.Tolerance && Math.Abs(floatDiff) < DayCloseRules.Tolerance)
            {
                return "Balanced";
            }

            // Short on either side is what an owner has to act on, even if the other is over.
            return cash <= -DayCloseRules.Tolerance || floatDiff <= -DayCloseRules.Tolerance ? "Short" : "Over";
        }
    }
}

public static class DayCloseRules
{
    /// <summary>Differences below one cedi are coins in a drawer, not a problem to chase.</summary>
    public const decimal Tolerance = 1.00m;

    /// <summary>No agent holds this much; a figure above it is a typing slip.</summary>
    public const decimal MaximumFigure = 10_000_000m;
}

public sealed class DayCloseRejectedException(string message) : Exception(message);

public interface IDayCloseService
{
    Task<DayCloseResult> CloseAsync(DayCloseRequest request, CancellationToken cancellationToken = default);

    /// <summary>The agent's latest close, or null if they have never closed.</summary>
    Task<DayCloseResult?> LatestAsync(Guid organizationId, Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Each agent's standing close for a business day — the latest if they counted twice —
    /// optionally for one branch. For the owner's closing page.
    /// </summary>
    Task<IReadOnlyList<DayCloseResult>> ForDayAsync(
        Guid organizationId, Guid? branchId, DateOnly businessDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// The people who traded on a business day and have not closed it — the ones an owner has
    /// to chase before the day can be called done.
    /// </summary>
    Task<IReadOnlyList<string>> NotClosedAsync(
        Guid organizationId, Guid? branchId, DateOnly businessDate, CancellationToken cancellationToken = default);
}
