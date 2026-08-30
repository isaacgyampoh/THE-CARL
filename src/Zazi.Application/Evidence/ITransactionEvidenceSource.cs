using Zazi.Domain;

namespace Zazi.Application.Evidence;

/// <summary>
/// An observation offered to Zazi, before anything has been validated or accepted.
/// </summary>
/// <remarks>
/// Platform-neutral by construction: an Android SMS, a keyed entry on an iPhone, a web form
/// submission and an imported statement line all arrive as this same shape. The pipeline
/// downstream cannot tell them apart except by <see cref="SourceType"/>, which is the point.
/// </remarks>
public sealed record EvidenceCapture(
    EvidenceSourceType SourceType,
    Guid BranchId,
    Guid? DeviceId,
    Guid? SessionId,
    string Provider,
    TransactionType ObservedType,
    decimal Amount,
    DateTimeOffset OccurredAtUtc,
    string? Currency = null,
    string? CustomerPhone = null,
    string? ProviderReference = null,
    DateTimeOffset? DeviceReceivedAtUtc = null,
    string? SenderAddress = null,
    string? RawMessage = null,
    string? ParserName = null,
    string? ParserVersion = null,
    string? ClientTransactionId = null,
    string? Notes = null);

/// <summary>
/// The outcome of offering evidence. Always carries the evidence id, including on rejection.
/// </summary>
/// <remarks>
/// <paramref name="FinancialTransactionId"/> is null whenever nothing was posted — which is
/// the normal outcome for incomplete or unclassified observations, not an error.
/// </remarks>
public sealed record EvidenceCaptureResult(
    Guid EvidenceId,
    string Fingerprint,
    TransactionLifecycleState State,
    Guid? FinancialTransactionId,
    bool IsDuplicate,
    string? OutcomeReason);

/// <summary>
/// A way Zazi obtains transaction evidence.
/// </summary>
/// <remarks>
/// <para>
/// Implementations adapt one acquisition method — Android SMS, manual entry, an imported
/// statement, a future gateway — into the <b>one</b> evidence pipeline. They do not decide
/// financial outcomes.
/// </para>
/// <para>
/// <b>The rule an implementation may never break:</b> an evidence source produces
/// <see cref="TransactionEvidence"/> and nothing else. It never creates a
/// <see cref="FinancialTransaction"/> directly and never touches a balance. The path is
/// fixed:
/// </para>
/// <code>
/// Evidence source → TransactionEvidence → SmsEvidencePolicy → FinancialTransaction
///                 → LedgerPolicy → balance projection → reconciliation
/// </code>
/// <para>
/// Bypassing that path is how a UI ends up moving money.
/// </para>
/// </remarks>
public interface ITransactionEvidenceSource
{
    /// <summary>Which acquisition method this source represents.</summary>
    EvidenceSourceType SourceType { get; }

    /// <summary>
    /// Whether this source is usable on the given platform.
    /// </summary>
    /// <remarks>
    /// Consulted so a client is never offered a capture method its hardware cannot perform —
    /// an iPhone must not be shown an SMS-capture option that cannot work.
    /// </remarks>
    bool IsAvailableOn(DeviceType deviceType);

    /// <summary>
    /// Records the observation and runs it through the evidence pipeline.
    /// </summary>
    /// <param name="capture">The observation. All fields are untrusted client input.</param>
    /// <param name="organizationId">
    /// Resolved server-side from the authenticated context. Never supplied by a client.
    /// </param>
    /// <param name="submittedByUserId">The authenticated user, for accountability.</param>
    Task<EvidenceCaptureResult> CaptureAsync(
        EvidenceCapture capture,
        Guid organizationId,
        Guid submittedByUserId,
        CancellationToken cancellationToken = default);
}
