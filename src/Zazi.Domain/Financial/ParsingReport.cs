namespace Zazi.Domain;

/// <summary>
/// An agent's report that Zazi read one of their messages wrongly, with the message.
/// </summary>
/// <remarks>
/// <para>
/// The parser is verified against a corpus of messages that were written rather than captured.
/// Two defects found in a single week — one network's transactions attributed to another, and a
/// trailing balance reminder reversing a transaction's direction — were both found by inventing
/// a more realistic message, which is fair evidence that inventing more would find more.
/// </para>
/// <para>
/// The handset's evidence-quality gate catches messages the parser cannot read: they are held
/// for human classification rather than posted. It cannot catch a message the parser reads
/// confidently and wrongly, which is what both of those defects produced. The only thing that
/// notices those is the agent, who knows what they just did.
/// </para>
/// <para>
/// So this is the one path by which a real provider message reaches the people who maintain the
/// parser. Raw message bodies deliberately do not sync — the ordinary sync payload carries the
/// parsed result and nothing else — which is right for a system holding other people's
/// financial correspondence, and it means the only body that ever leaves a handset is one an
/// agent chose to send, about a transaction they are telling us is wrong.
/// </para>
/// </remarks>
public sealed class ParsingReport : AggregateRoot
{
    public Guid OrganizationId { get; set; }

    /// <summary>The agent who sent the report.</summary>
    public Guid ReportedByUserId { get; set; }

    public Guid? BranchId { get; set; }
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// The handset's id for the transaction being reported.
    /// </summary>
    /// <remarks>
    /// Not a foreign key. A report is most useful precisely when the transaction never reached
    /// the server, and refusing the report because its subject is missing would drop the
    /// evidence in the case that needs it most.
    /// </remarks>
    public string ClientTransactionId { get; set; } = string.Empty;

    /// <summary>The provider message, exactly as the handset received it.</summary>
    public string RawMessage { get; set; } = string.Empty;

    /// <summary>The shortcode or sender name the message arrived from.</summary>
    public string? SenderIdentity { get; set; }

    /// <summary>What Zazi made of it: the network it was filed under.</summary>
    public string ObservedNetwork { get; set; } = Networks.Unspecified;

    /// <summary>What Zazi made of it: the direction it was filed as.</summary>
    public TransactionType ObservedType { get; set; }

    /// <summary>The amount Zazi read, in minor units.</summary>
    public long ObservedAmountMinor { get; set; }

    /// <summary>What the agent says actually happened.</summary>
    public ParsingReportVerdict Verdict { get; set; }

    /// <summary>Anything the agent chose to add, in their own words.</summary>
    public string? Note { get; set; }

    /// <summary>Which build read the message, so a report can be tied to a parser version.</summary>
    public string? ParserVersion { get; set; }

    public string? AppVersion { get; set; }

    /// <summary>Set once someone has looked at it, so a queue can be worked through.</summary>
    public DateTimeOffset? ReviewedAtUtc { get; set; }

    public Guid? ReviewedByUserId { get; set; }
}

/// <summary>
/// What the agent says was wrong, in the terms they would use at a counter.
/// </summary>
/// <remarks>
/// Deliberately coarse. An agent reporting a problem mid-shift will pick the first option that
/// is roughly right, so offering finer distinctions would buy precision that is not there. The
/// message itself carries the detail, and whoever reads it can classify properly.
/// </remarks>
public enum ParsingReportVerdict
{
    /// <summary>Something is wrong but the agent did not say what.</summary>
    Unspecified = 0,

    /// <summary>Recorded as money in when it was money out, or the reverse.</summary>
    WrongDirection = 1,

    /// <summary>Right direction, wrong figure.</summary>
    WrongAmount = 2,

    /// <summary>Filed against the wrong network.</summary>
    WrongNetwork = 3,

    /// <summary>Not a transaction at all — a balance notice, a promotion, a one-time code.</summary>
    NotATransaction = 4,

    /// <summary>A real transaction that never appeared.</summary>
    Missing = 5
}
