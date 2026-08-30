namespace Zazi.Domain;

/// <summary>
/// What Zazi <b>accepted</b> into the accounting ledger.
/// </summary>
/// <remarks>
/// <para>
/// A financial transaction only exists once evidence has been validated, classified, and
/// found not to be a duplicate. Its balance effect always comes from
/// <see cref="LedgerPolicy"/> — never from logic re-derived at the call site.
/// </para>
/// <para>
/// Rows are append-only in spirit: a mistake is corrected by an
/// <see cref="TransactionType.Adjustment"/> or <see cref="TransactionType.Reversal"/> that
/// references this one, never by editing the amount or type in place.
/// </para>
/// </remarks>
public sealed class FinancialTransaction : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? SessionId { get; set; }

    /// <summary>The authenticated agent who recorded it.</summary>
    public Guid AgentId { get; set; }

    public Guid? DeviceId { get; set; }

    /// <summary>The evidence this transaction was accepted from.</summary>
    public Guid? EvidenceId { get; set; }

    // ─── Financial facts ─────────────────────────────────────────────────────
    public string Network { get; set; } = string.Empty;
    public TransactionType Type { get; set; } = TransactionType.Unknown;

    /// <summary>Positive magnitude. Direction is carried by <see cref="Type"/>.</summary>
    public decimal Amount { get; set; }

    public string Currency { get; set; } = Money.DefaultCurrency;

    /// <summary>
    /// Cash movement applied to the branch, resolved by <see cref="LedgerPolicy"/> at
    /// acceptance and stored so a projection can be rebuilt without re-deriving direction.
    /// </summary>
    public decimal CashDelta { get; set; }

    /// <summary>Float movement applied to the branch, resolved by <see cref="LedgerPolicy"/>.</summary>
    public decimal FloatDelta { get; set; }

    public string? CustomerPhoneNumber { get; set; }
    public string? ProviderReference { get; set; }

    /// <summary>Provider-reported event time.</summary>
    public DateTimeOffset TransactionAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Server acceptance time. Authoritative for ordering and reporting.</summary>
    public DateTimeOffset AcceptedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    // ─── Provenance and trust ────────────────────────────────────────────────
    public TransactionSource Source { get; set; } = TransactionSource.Manual;
    public TransactionLifecycleState State { get; set; } = TransactionLifecycleState.Accepted;
    public decimal ConfidenceScore { get; set; }

    // ─── Idempotency ─────────────────────────────────────────────────────────

    /// <summary>
    /// Device-generated identity, unique within the organization. Guarantees that retrying
    /// a submission produces one row. See docs/OFFLINE_TRANSACTION_CONTRACT.md.
    /// </summary>
    public string? ClientTransactionId { get; set; }

    /// <summary>Canonical evidence fingerprint, unique within the organization.</summary>
    public string? EvidenceFingerprint { get; set; }

    // ─── Corrections ─────────────────────────────────────────────────────────

    /// <summary>Set when <see cref="Type"/> is <see cref="TransactionType.Reversal"/>.</summary>
    public Guid? ReversesTransactionId { get; set; }

    /// <summary>Set when <see cref="Type"/> is <see cref="TransactionType.Adjustment"/>.</summary>
    public Guid? AdjustsTransactionId { get; set; }

    /// <summary>Required justification for adjustments and reversals.</summary>
    public string? CorrectionReason { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// Applies a movement resolved by <see cref="LedgerPolicy"/>. Centralised so the stored
    /// deltas can never disagree with the policy.
    /// </summary>
    public void ApplyMovement(LedgerMovement movement)
    {
        CashDelta = movement.CashDelta;
        FloatDelta = movement.FloatDelta;
    }
}
