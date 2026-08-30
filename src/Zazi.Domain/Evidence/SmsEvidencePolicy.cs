namespace Zazi.Domain;

/// <summary>
/// The single authority for how much trust a parsed SMS earns, and whether it may post
/// without a person looking at it.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors the Android client's <c>ParsedSms</c> rules exactly. The two implementations
/// cannot share code, so they are kept in step by a shared fixture corpus
/// (<c>contracts/sms-contract-fixtures.json</c>) that both test suites assert against.
/// </para>
/// <para>
/// <b>Why a provider reference is mandatory for auto-posting.</b> Every real provider
/// transaction message carries one. A message without it is truncated, promotional, or an
/// unrecognised template. Without this rule a delivery truncated from
/// <c>"Cash In of GHS 500.00 ... Ref: MP240815.1201.A00001"</c> down to
/// <c>"Cash In of GHS 5"</c> parses as a perfectly plausible GHS 5 cash-in and posts
/// silently, understating the agent's till by GHS 495.
/// </para>
/// <para>
/// The bias is deliberate: hold for review rather than guess and post money. A held
/// transaction costs an agent seconds; a wrongly posted one costs them cash.
/// </para>
/// </remarks>
public static class SmsEvidencePolicy
{
    /// <summary>
    /// Below this, evidence is preserved for review and never posted automatically.
    /// Set high on purpose — see the remarks above.
    /// </summary>
    public const decimal MinimumAutoPostConfidence = 0.80m;

    // Confidence bands. Each names the specific deficiency so an operator reviewing held
    // evidence can see why it was held.
    private const decimal UnclassifiedConfidence = 0.30m;
    private const decimal NoUsableAmountConfidence = 0.40m;
    private const decimal NoProviderReferenceConfidence = 0.50m;
    private const decimal CompleteConfidence = 0.95m;

    /// <summary>Confidence for a recognised provider's parser.</summary>
    /// <param name="transactionTypeToken">Parser verdict, e.g. "CASH_IN". "UNKNOWN" means unclassified.</param>
    /// <param name="amount">Extracted amount; zero or negative means none was recovered.</param>
    /// <param name="providerReference">The provider's own transaction reference, if found.</param>
    public static decimal ScoreProviderEvidence(
        string? transactionTypeToken,
        decimal amount,
        string? providerReference)
    {
        if (string.IsNullOrWhiteSpace(transactionTypeToken)
            || transactionTypeToken.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
        {
            return UnclassifiedConfidence;
        }

        if (amount <= 0m)
        {
            return NoUsableAmountConfidence;
        }

        if (string.IsNullOrWhiteSpace(providerReference))
        {
            // Recognised provider and a plausible amount, but nothing identifying the
            // transaction. This is the truncation case.
            return NoProviderReferenceConfidence;
        }

        return CompleteConfidence;
    }

    /// <summary>
    /// Confidence for the generic fallback parser.
    /// </summary>
    /// <remarks>
    /// Deliberately fixed below <see cref="MinimumAutoPostConfidence"/> whatever it manages
    /// to extract. An unrecognised template is exactly where a confident guess is most
    /// dangerous, so its output always reaches a person.
    /// </remarks>
    public static decimal ScoreGenericEvidence() => 0.20m;

    /// <summary>
    /// Whether parsed evidence is complete enough to post without review. Type-based rules
    /// (Unknown, Reversal, Adjustment) remain <see cref="LedgerPolicy"/>'s decision; this
    /// governs only evidence quality.
    /// </summary>
    public static bool MeetsAutoPostBar(decimal confidence, decimal amount, TransactionType type) =>
        confidence >= MinimumAutoPostConfidence
        && amount >= LedgerPolicy.MinimumAmount
        && type != TransactionType.Unknown;
}
