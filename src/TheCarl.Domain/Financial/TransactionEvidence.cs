namespace TheCarl.Domain;

/// <summary>How a transaction came to THE CARL's attention.</summary>
public enum EvidenceSourceType
{
    /// <summary>SMS read on an Android device running THE CARL.</summary>
    AndroidSms = 0,

    /// <summary>Keyed in by a person. Lower trust than parsed provider evidence.</summary>
    ManualEntry = 1,

    /// <summary>Forwarded by an authenticated GSM gateway. Not yet implemented.</summary>
    GsmGateway = 2,

    /// <summary>Relayed by a companion device. Not yet implemented.</summary>
    Relay = 3,

    /// <summary>Supplied by a provider API integration. Not yet implemented.</summary>
    ProviderApi = 4,

    /// <summary>
    /// Read from a provider statement or export rather than observed live.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ManualEntry"/>: an import is a bulk transcription of a
    /// provider's own record, not a person's recollection, and carries different trust.
    /// <para>
    /// iOS and Web manual capture deliberately reuse <see cref="ManualEntry"/>. A source type
    /// per platform would fragment the evidence contract for no gain — the platform is
    /// already recorded on the device.
    /// </para>
    /// </remarks>
    ImportedEvidence = 5
}

/// <summary>
/// What was <b>observed</b> — an unverified account of a transaction.
/// </summary>
/// <remarks>
/// <para>
/// Evidence is deliberately separate from <see cref="FinancialTransaction"/>. Evidence is
/// a claim; a financial transaction is a claim THE CARL has accepted into the ledger.
/// Conflating them means a parser failure, a duplicate delivery or a malformed SMS becomes
/// accounting data.
/// </para>
/// <para>
/// Evidence is created for <b>every</b> observation, including rejected and duplicate ones,
/// because the record of what was seen and why it was not posted is itself auditable.
/// Many evidence rows may exist for a single financial transaction; at most one financial
/// transaction may exist per accepted evidence row.
/// </para>
/// </remarks>
public sealed class TransactionEvidence : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? SessionId { get; set; }

    /// <summary>The authenticated user who submitted this evidence.</summary>
    public Guid SubmittedByUserId { get; set; }

    public EvidenceSourceType SourceType { get; set; } = EvidenceSourceType.ManualEntry;

    // ─── Observed fields ─────────────────────────────────────────────────────
    public string Provider { get; set; } = string.Empty;
    public string? SenderAddress { get; set; }
    public string? CustomerPhoneNumber { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = Money.DefaultCurrency;
    public TransactionType ObservedType { get; set; } = TransactionType.Unknown;
    public string? ProviderReference { get; set; }

    /// <summary>When the provider says the event happened.</summary>
    public DateTimeOffset? OccurredAtUtc { get; set; }

    /// <summary>When the device observed it. Client-supplied and therefore untrusted.</summary>
    public DateTimeOffset? DeviceReceivedAtUtc { get; set; }

    /// <summary>When the server received it. Server-generated and authoritative.</summary>
    public DateTimeOffset ServerReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    // ─── Provenance ──────────────────────────────────────────────────────────

    /// <summary>Canonical fingerprint; see <see cref="EvidenceFingerprint"/>.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Scheme version that produced <see cref="Fingerprint"/>.</summary>
    public string FingerprintVersion { get; set; } = EvidenceFingerprint.Version;

    /// <summary>Hash of the raw message, for exact-redelivery detection.</summary>
    public string? RawHash { get; set; }

    /// <summary>
    /// Parser identity and version. Recorded so that when a provider changes its SMS
    /// template, historical rows remain traceable to the parser that interpreted them.
    /// </summary>
    public string ParserName { get; set; } = string.Empty;
    public string ParserVersion { get; set; } = string.Empty;

    /// <summary>Parser confidence, 0.0000–1.0000.</summary>
    public decimal ConfidenceScore { get; set; }

    /// <summary>
    /// Raw message text. Nullable so it can be purged on its retention schedule without
    /// breaking duplicate detection, which relies on the fingerprint.
    /// </summary>
    public string? RawMessage { get; set; }

    public DateTimeOffset? RawMessagePurgedAtUtc { get; set; }

    // ─── Outcome ─────────────────────────────────────────────────────────────
    public TransactionLifecycleState State { get; set; } = TransactionLifecycleState.Detected;

    /// <summary>Set once this evidence has been accepted into the ledger.</summary>
    public Guid? FinancialTransactionId { get; set; }

    /// <summary>Why the evidence was rejected or held, when applicable.</summary>
    public string? OutcomeReason { get; set; }

    public bool IsDuplicate { get; set; }

    /// <summary>The earlier evidence row this one duplicates.</summary>
    public Guid? DuplicateOfEvidenceId { get; set; }
}
