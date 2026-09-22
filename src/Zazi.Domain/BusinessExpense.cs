namespace Zazi.Domain;

/// <summary>What it costs to run the business, so the owner can see profit and not just volume.</summary>
public enum ExpenseCategory
{
    /// <summary>Airtime, data, and what the networks charge to move money.</summary>
    Airtime = 0,
    Rent = 1,
    Transport = 2,
    Wages = 3,
    Electricity = 4,
    Equipment = 5,
    Other = 6
}

/// <summary>
/// One cost the business paid.
/// </summary>
/// <remarks>
/// <para>
/// Commission tells an owner what they earned; it does not tell them what they kept. Rent,
/// airtime, transport and wages are what stand between the two, and an agent business that
/// cannot see them is guessing at whether it is worth running.
/// </para>
/// <para>
/// Recorded, never edited: a correction is a second record with a negative amount, so the
/// history shows what was believed at the time. The same rule the ledger follows.
/// </para>
/// </remarks>
public sealed class BusinessExpense : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid? BranchId { get; set; }

    /// <summary>The agent it relates to, when it relates to one. Wages, a worker's transport.</summary>
    public Guid? AgentId { get; set; }

    public ExpenseCategory Category { get; set; }

    /// <summary>Positive for money spent; negative corrects an earlier record.</summary>
    public decimal Amount { get; set; }

    public string Currency { get; set; } = Money.DefaultCurrency;

    /// <summary>The day the money went out, which is not always the day it was typed in.</summary>
    public DateOnly SpentOn { get; set; }

    public string? Note { get; set; }
    public Guid RecordedByUserId { get; set; }

    /// <summary>One attempt to record, so a repeated submission is stored once.</summary>
    public string? SubmissionToken { get; set; }
}
