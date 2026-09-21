namespace Zazi.Domain;

public enum FloatRequestStatus
{
    Pending = 0,
    Approved = 1,
    Declined = 2
}

/// <summary>
/// An agent asking their owner for float — "I am running out of MTN, send me 500".
/// </summary>
/// <remarks>
/// Today this happens by phone call and is never written down, so the float given is the
/// first thing missing when the books do not agree. A request is recorded when it is made; an
/// approval records the float given against the agent in the same step, so the two cannot
/// drift apart. Decided once: a request is never reopened.
/// </remarks>
public sealed class FloatRequest : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public Guid AgentId { get; set; }
    public string Network { get; set; } = string.Empty;
    public decimal Amount { get; set; }

    /// <summary>
    /// Four digits the owner quotes to answer by SMS ("OK 4821"). Unique among the business's
    /// pending requests, so a reply cannot land on the wrong one.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>"App" or "SMS".</summary>
    public string Channel { get; set; } = string.Empty;

    public FloatRequestStatus Status { get; set; } = FloatRequestStatus.Pending;
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
    public Guid? DecidedByUserId { get; set; }

    /// <summary>"Portal" or "SMS": how the owner answered.</summary>
    public string? DecidedVia { get; set; }
}
