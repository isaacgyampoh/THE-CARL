using Zazi.Domain;

namespace Zazi.Application.Parsing;

/// <summary>
/// An agent telling us Zazi read one of their messages wrongly, and sending the message.
/// </summary>
/// <remarks>
/// Tenancy, the reporting user and the branch are taken from the caller's token, never from the
/// body, so an agent cannot file a report against another organisation's transaction.
/// </remarks>
public sealed record SubmitParsingReportRequest(
    string ClientTransactionId,
    string RawMessage,
    ParsingReportVerdict Verdict,
    string? SenderIdentity = null,
    string? ObservedNetwork = null,
    TransactionType ObservedType = TransactionType.Unknown,
    long ObservedAmountMinor = 0,
    string? Note = null,
    string? ParserVersion = null,
    string? AppVersion = null);

/// <summary>
/// What the handset is told back. Deliberately thin: the agent needs to know it arrived.
/// </summary>
/// <param name="AlreadyReported">
/// True when this agent had already reported this transaction. Not an error — a second tap on
/// a slow connection is the likeliest cause — so the handset can say "thank you" either way.
/// </param>
public sealed record ParsingReportReceipt(Guid ReportId, bool AlreadyReported);

/// <summary>A report as whoever maintains the parser reads it.</summary>
public sealed record ParsingReportDto(
    Guid Id,
    Guid OrganizationId,
    string ClientTransactionId,
    string RawMessage,
    string? SenderIdentity,
    string ObservedNetwork,
    TransactionType ObservedType,
    long ObservedAmountMinor,
    ParsingReportVerdict Verdict,
    string? Note,
    string? ParserVersion,
    string? AppVersion,
    DateTimeOffset ReportedAtUtc,
    DateTimeOffset? ReviewedAtUtc);

public interface IParsingReportService
{
    /// <summary>Records a report. Idempotent per agent and transaction.</summary>
    Task<ParsingReportReceipt> SubmitAsync(
        SubmitParsingReportRequest request,
        Guid organizationId,
        Guid reportedByUserId,
        Guid? branchId,
        Guid? deviceId,
        CancellationToken cancellationToken = default);

    /// <summary>The unreviewed backlog for one organisation, oldest first.</summary>
    Task<IReadOnlyList<ParsingReportDto>> ListUnreviewedAsync(
        Guid organizationId,
        int limit = 100,
        CancellationToken cancellationToken = default);
}
