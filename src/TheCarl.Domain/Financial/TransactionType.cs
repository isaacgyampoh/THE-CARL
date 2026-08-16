namespace TheCarl.Domain;

/// <summary>
/// Classification of a financial transaction, stated from the agent's books.
/// </summary>
/// <remarks>
/// <para>
/// <b>The numeric values are part of the persisted contract.</b> They are stored as
/// integers in PostgreSQL, so reordering or renumbering existing members silently
/// reclassifies historical financial rows. New members must be appended with new values.
/// </para>
/// <para>
/// <c>CashIn</c> and <c>CashOut</c> intentionally keep the ordinals of the former
/// <c>Deposit</c> (0) and <c>Withdrawal</c> (1). A customer deposit *is* a cash-in and a
/// customer withdrawal *is* a cash-out, so existing rows keep their correct meaning without
/// a data migration.
/// </para>
/// </remarks>
public enum TransactionType
{
    /// <summary>Customer hands physical cash to the agent and receives e-money.</summary>
    CashIn = 0,

    /// <summary>Agent hands physical cash to the customer and receives e-money.</summary>
    CashOut = 1,

    /// <summary>
    /// Movement of e-money that is not a till-side cash event. Carries no implicit cash or
    /// float movement because source and destination semantics are not derivable from an SMS.
    /// </summary>
    Transfer = 2,

    /// <summary>
    /// Cancels a previously accepted transaction. Must reference the original via
    /// <see cref="FinancialTransaction.ReversesTransactionId"/>; its ledger effect is the
    /// exact inverse of the original's.
    /// </summary>
    Reversal = 3,

    /// <summary>Commission credited by the provider to the agent's e-money float.</summary>
    Commission = 4,

    /// <summary>
    /// Deliberate correction raised by reconciliation. Carries explicit signed cash and
    /// float deltas rather than inferring direction, because an adjustment can move either
    /// side in either direction.
    /// </summary>
    Adjustment = 5,

    /// <summary>
    /// The classifier could not determine a type. Never affects balances and always
    /// requires human review.
    /// </summary>
    Unknown = 6
}
