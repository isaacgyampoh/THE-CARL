namespace Zazi.Domain;

/// <summary>
/// An agent's count at the end of a day: the cash in the drawer and the float on the SIMs,
/// set against what Zazi expected them to be holding.
/// </summary>
/// <remarks>
/// <para>
/// Expected is carried forward from the agent's previous close, plus every transaction
/// recorded for them since. It does not depend on an owner having entered an opening balance,
/// which most never do. The first close therefore has nothing to compare with: it is the
/// baseline, and says so, rather than reporting a "shortage" of everything the agent holds.
/// </para>
/// <para>
/// Never edited. A recount is a second close, and the latest is the one that stands — the
/// first stays on record, which is what an owner looking into a shortage needs to see.
/// </para>
/// </remarks>
public sealed class DayClose : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public Guid AgentId { get; set; }

    /// <summary>The moment of the count. Transactions after this belong to the next close.</summary>
    public DateTimeOffset ClosedAtUtc { get; set; }

    /// <summary>The day being closed. Ghana keeps GMT, so the UTC date is the business date.</summary>
    public DateOnly BusinessDate { get; set; }

    /// <summary>"App", "SMS" or "Portal".</summary>
    public string Channel { get; set; } = string.Empty;

    public decimal CountedCash { get; set; }
    public decimal CountedFloat { get; set; }

    /// <summary>Null on the first close, which sets the baseline.</summary>
    public decimal? ExpectedCash { get; set; }
    public decimal? ExpectedFloat { get; set; }

    /// <summary>Net movement recorded since the previous close.</summary>
    public decimal CashMovement { get; set; }
    public decimal FloatMovement { get; set; }
    public int TransactionCount { get; set; }

    public Guid? PreviousCloseId { get; set; }
    public string? Note { get; set; }

    /// <summary>Counted minus expected: positive is over, negative is short.</summary>
    public decimal? CashDifference => ExpectedCash is { } expected ? CountedCash - expected : null;
    public decimal? FloatDifference => ExpectedFloat is { } expected ? CountedFloat - expected : null;
}
