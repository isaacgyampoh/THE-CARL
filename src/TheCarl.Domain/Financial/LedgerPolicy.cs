namespace TheCarl.Domain;

/// <summary>
/// The signed effect a transaction has on an agent's two balances.
/// Positive means the balance increases.
/// </summary>
/// <param name="CashDelta">Change to physical cash in the till.</param>
/// <param name="FloatDelta">Change to the network e-money float.</param>
/// <param name="RequiresReview">
/// True when the transaction must not post automatically and needs human classification.
/// </param>
/// <param name="Rationale">Human-readable explanation, recorded on the audit trail.</param>
public readonly record struct LedgerMovement(
    decimal CashDelta,
    decimal FloatDelta,
    bool RequiresReview,
    string Rationale)
{
    /// <summary>No balance effect.</summary>
    public static LedgerMovement None(string rationale) => new(0m, 0m, false, rationale);

    /// <summary>No balance effect, and the transaction is held for review.</summary>
    public static LedgerMovement Review(string rationale) => new(0m, 0m, true, rationale);

    public bool IsZero => CashDelta == 0m && FloatDelta == 0m;

    /// <summary>The exact inverse, used when reversing an accepted transaction.</summary>
    public LedgerMovement Inverse(string rationale) =>
        new(-CashDelta, -FloatDelta, RequiresReview, rationale);
}

/// <summary>
/// The single authoritative source of balance direction in THE CARL.
/// </summary>
/// <remarks>
/// <para>
/// Every balance projection — live ledger updates, session reconciliation, dashboard
/// aggregates, analytics — must derive movement from this class. Direction was previously
/// re-derived from transaction names inside individual services, and two of them disagreed
/// with each other, so reconciliation differences were wrong by twice the transaction value.
/// </para>
/// <para><b>Direction, stated from the agent's books:</b></para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Cash-in</b> — the customer hands over physical cash and the agent sends e-money.
/// Agent cash <b>rises</b>; agent float <b>falls</b>.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Cash-out</b> — the customer sends e-money and the agent pays out physical cash.
/// Agent cash <b>falls</b>; agent float <b>rises</b>.
/// </description>
/// </item>
/// </list>
/// </remarks>
public static class LedgerPolicy
{
    /// <summary>Currency scale for GHS. All monetary values carry two decimal places.</summary>
    public const int CurrencyScale = 2;

    /// <summary>
    /// Storage scale. Wider than <see cref="CurrencyScale"/> so intermediate values such as
    /// commission rates do not lose precision before rounding for presentation.
    /// </summary>
    public const int StorageScale = 4;

    public const int StoragePrecision = 18;

    /// <summary>Smallest representable amount: one pesewa.</summary>
    public const decimal MinimumAmount = 0.01m;

    /// <summary>
    /// Resolves the balance movement for a transaction.
    /// </summary>
    /// <param name="type">The classified transaction type.</param>
    /// <param name="amount">Always a positive magnitude; direction comes from the type.</param>
    /// <param name="originalType">
    /// Required when <paramref name="type"/> is <see cref="TransactionType.Reversal"/>:
    /// the type of the transaction being reversed.
    /// </param>
    /// <param name="explicitCashDelta">
    /// Required when <paramref name="type"/> is <see cref="TransactionType.Adjustment"/>.
    /// </param>
    /// <param name="explicitFloatDelta">
    /// Required when <paramref name="type"/> is <see cref="TransactionType.Adjustment"/>.
    /// </param>
    public static LedgerMovement MovementFor(
        TransactionType type,
        decimal amount,
        TransactionType? originalType = null,
        decimal? explicitCashDelta = null,
        decimal? explicitFloatDelta = null)
    {
        if (type != TransactionType.Adjustment && amount < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Amount must be a positive magnitude; direction is determined by the transaction type.");
        }

        return type switch
        {
            TransactionType.CashIn => new LedgerMovement(
                CashDelta: +amount,
                FloatDelta: -amount,
                RequiresReview: false,
                Rationale: "Cash-in: agent receives physical cash and sends e-money."),

            TransactionType.CashOut => new LedgerMovement(
                CashDelta: -amount,
                FloatDelta: +amount,
                RequiresReview: false,
                Rationale: "Cash-out: agent pays out physical cash and receives e-money."),

            // A transfer moves e-money between wallets. Which side of the agent's books it
            // touches, if either, cannot be inferred from the message alone, so nothing is
            // posted rather than guessing.
            TransactionType.Transfer => LedgerMovement.None(
                "Transfer: recorded without balance movement because source and destination semantics are not derivable."),

            TransactionType.Commission => new LedgerMovement(
                CashDelta: 0m,
                FloatDelta: +amount,
                RequiresReview: false,
                Rationale: "Commission: provider credits the agent's e-money float."),

            TransactionType.Reversal => ReversalMovement(amount, originalType),

            TransactionType.Adjustment => AdjustmentMovement(explicitCashDelta, explicitFloatDelta),

            // Never post an unclassified transaction. Doing so would let a parser failure
            // silently corrupt a branch's balances.
            TransactionType.Unknown => LedgerMovement.Review(
                "Unknown type: held for review and excluded from all balance projections."),

            _ => throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                "Unhandled transaction type. Every type must have an explicit accounting rule.")
        };
    }

    private static LedgerMovement ReversalMovement(decimal amount, TransactionType? originalType)
    {
        if (originalType is not { } original)
        {
            // A reversal with no original cannot have a known direction.
            return LedgerMovement.Review(
                "Reversal without a referenced original transaction: held for review.");
        }

        if (original is TransactionType.Reversal)
        {
            return LedgerMovement.Review(
                "Reversal of a reversal: held for review rather than resolved automatically.");
        }

        var originalMovement = MovementFor(original, amount);
        return originalMovement.Inverse($"Reversal of {original}: exact inverse of the original movement.");
    }

    private static LedgerMovement AdjustmentMovement(decimal? cashDelta, decimal? floatDelta)
    {
        if (cashDelta is null && floatDelta is null)
        {
            throw new ArgumentException(
                "An adjustment must carry an explicit cash delta, float delta, or both. " +
                "Direction cannot be inferred for a correction.");
        }

        return new LedgerMovement(
            CashDelta: cashDelta ?? 0m,
            FloatDelta: floatDelta ?? 0m,
            RequiresReview: false,
            Rationale: "Adjustment: explicit correction raised by an authorised reconciliation workflow.");
    }

    /// <summary>
    /// Whether a type may post automatically from parsed evidence without human review.
    /// </summary>
    /// <remarks>
    /// Reversals and adjustments are excluded because they either need a reference to an
    /// original or explicit signed deltas that a parser cannot supply.
    /// </remarks>
    public static bool CanPostAutomatically(TransactionType type) => type switch
    {
        TransactionType.CashIn => true,
        TransactionType.CashOut => true,
        TransactionType.Transfer => true,
        TransactionType.Commission => true,
        _ => false
    };

    /// <summary>Rounds to currency scale using banker's-rounding-free half-away-from-zero.</summary>
    public static decimal RoundToCurrency(decimal value) =>
        Math.Round(value, CurrencyScale, MidpointRounding.AwayFromZero);

    /// <summary>Validates a transaction amount for posting.</summary>
    public static void ValidateAmount(decimal amount)
    {
        if (amount < MinimumAmount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount,
                $"Transaction amount must be at least {MinimumAmount:0.00}.");
        }

        if (decimal.Round(amount, StorageScale) != amount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount,
                $"Transaction amount carries more than {StorageScale} decimal places.");
        }
    }
}
