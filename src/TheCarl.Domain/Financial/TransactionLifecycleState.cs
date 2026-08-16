namespace TheCarl.Domain;

/// <summary>
/// The lifecycle of a transaction from observation to ledger outcome.
/// </summary>
/// <remarks>
/// SMS <i>detected</i> is not the same thing as a financial transaction <i>accepted</i>.
/// Everything up to and including <see cref="PendingReview"/> is evidence; only
/// <see cref="Accepted"/> and later states have a ledger effect.
/// </remarks>
public enum TransactionLifecycleState
{
    /// <summary>Evidence observed but not yet parsed.</summary>
    Detected = 0,

    /// <summary>A parser extracted structured fields. Still evidence, not ledger data.</summary>
    Parsed = 1,

    /// <summary>Parsed but not trusted enough to post: low confidence or an unknown type.</summary>
    PendingReview = 2,

    /// <summary>Validated and posted to the ledger. This is the first state with a balance effect.</summary>
    Accepted = 3,

    /// <summary>Rejected. Never had, and never will have, a ledger effect.</summary>
    Rejected = 4,

    /// <summary>Accepted locally and confirmed as durable on the server.</summary>
    Synced = 5,

    /// <summary>Superseded by an accepted <see cref="TransactionType.Reversal"/>.</summary>
    Reversed = 6,

    /// <summary>Corrected by an <see cref="TransactionType.Adjustment"/>; the original row is unchanged.</summary>
    Adjusted = 7
}

public static class TransactionLifecycle
{
    /// <summary>States in which a transaction contributes to balance projections.</summary>
    public static bool AffectsLedger(this TransactionLifecycleState state) => state switch
    {
        TransactionLifecycleState.Accepted => true,
        TransactionLifecycleState.Synced => true,
        // A reversed or adjusted original stays on the ledger: the correcting entry carries
        // the offset. Removing the original would destroy the audit trail.
        TransactionLifecycleState.Reversed => true,
        TransactionLifecycleState.Adjusted => true,
        _ => false
    };

    /// <summary>Whether a lifecycle transition is permitted.</summary>
    public static bool CanTransitionTo(this TransactionLifecycleState from, TransactionLifecycleState to)
    {
        if (from == to)
        {
            return true;
        }

        return from switch
        {
            TransactionLifecycleState.Detected =>
                to is TransactionLifecycleState.Parsed or TransactionLifecycleState.Rejected,

            TransactionLifecycleState.Parsed =>
                to is TransactionLifecycleState.PendingReview
                    or TransactionLifecycleState.Accepted
                    or TransactionLifecycleState.Rejected,

            TransactionLifecycleState.PendingReview =>
                to is TransactionLifecycleState.Accepted or TransactionLifecycleState.Rejected,

            TransactionLifecycleState.Accepted =>
                to is TransactionLifecycleState.Synced
                    or TransactionLifecycleState.Reversed
                    or TransactionLifecycleState.Adjusted,

            TransactionLifecycleState.Synced =>
                to is TransactionLifecycleState.Reversed or TransactionLifecycleState.Adjusted,

            // Terminal: a rejected transaction cannot be resurrected, and a reversed or
            // adjusted one is corrected by a new entry rather than by mutating this one.
            TransactionLifecycleState.Rejected => false,
            TransactionLifecycleState.Reversed => false,
            TransactionLifecycleState.Adjusted => to is TransactionLifecycleState.Reversed,

            _ => false
        };
    }
}
