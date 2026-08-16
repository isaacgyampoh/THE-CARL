using TheCarl.Domain;

namespace TheCarl.UnitTests;

/// <summary>
/// The direction of money. These tests are the specification: if one fails, either the
/// policy is wrong or the accounting model changed, and both demand a deliberate decision.
/// </summary>
public class LedgerPolicyTests
{
    [Fact]
    public void CashIn_RaisesCashAndLowersFloat()
    {
        // The customer hands over ₵500 and the agent sends ₵500 of e-money.
        var movement = LedgerPolicy.MovementFor(TransactionType.CashIn, 500m);

        Assert.Equal(+500m, movement.CashDelta);
        Assert.Equal(-500m, movement.FloatDelta);
        Assert.False(movement.RequiresReview);
    }

    [Fact]
    public void CashOut_LowersCashAndRaisesFloat()
    {
        var movement = LedgerPolicy.MovementFor(TransactionType.CashOut, 500m);

        Assert.Equal(-500m, movement.CashDelta);
        Assert.Equal(+500m, movement.FloatDelta);
    }

    [Fact]
    public void CashInAndCashOut_AreExactMirrors()
    {
        var cashIn = LedgerPolicy.MovementFor(TransactionType.CashIn, 250m);
        var cashOut = LedgerPolicy.MovementFor(TransactionType.CashOut, 250m);

        Assert.Equal(cashIn.CashDelta, -cashOut.CashDelta);
        Assert.Equal(cashIn.FloatDelta, -cashOut.FloatDelta);
    }

    [Fact]
    public void CashInAndCashOut_PreserveTotalValue()
    {
        // Cash and float move in opposite directions by the same magnitude, so the agent's
        // combined position is unchanged by a till transaction. Only commission changes it.
        var movement = LedgerPolicy.MovementFor(TransactionType.CashIn, 731.45m);

        Assert.Equal(0m, movement.CashDelta + movement.FloatDelta);
    }

    [Fact]
    public void Transfer_DoesNotMoveEitherBalance()
    {
        var movement = LedgerPolicy.MovementFor(TransactionType.Transfer, 100m);

        Assert.True(movement.IsZero);
        Assert.False(movement.RequiresReview);
    }

    [Fact]
    public void Commission_CreditsFloatOnly()
    {
        var movement = LedgerPolicy.MovementFor(TransactionType.Commission, 12.75m);

        Assert.Equal(0m, movement.CashDelta);
        Assert.Equal(+12.75m, movement.FloatDelta);
    }

    [Fact]
    public void Unknown_NeverAffectsBalancesAndDemandsReview()
    {
        var movement = LedgerPolicy.MovementFor(TransactionType.Unknown, 9_999m);

        Assert.True(movement.IsZero);
        Assert.True(movement.RequiresReview);
    }

    [Theory]
    [InlineData(TransactionType.CashIn)]
    [InlineData(TransactionType.CashOut)]
    [InlineData(TransactionType.Commission)]
    public void Reversal_IsTheExactInverseOfTheOriginal(TransactionType originalType)
    {
        var original = LedgerPolicy.MovementFor(originalType, 300m);
        var reversal = LedgerPolicy.MovementFor(TransactionType.Reversal, 300m, originalType);

        Assert.Equal(-original.CashDelta, reversal.CashDelta);
        Assert.Equal(-original.FloatDelta, reversal.FloatDelta);

        // Original plus reversal nets to nothing on both sides.
        Assert.Equal(0m, original.CashDelta + reversal.CashDelta);
        Assert.Equal(0m, original.FloatDelta + reversal.FloatDelta);
    }

    [Fact]
    public void Reversal_WithoutAnOriginal_IsHeldForReview()
    {
        var movement = LedgerPolicy.MovementFor(TransactionType.Reversal, 300m);

        Assert.True(movement.IsZero);
        Assert.True(movement.RequiresReview);
    }

    [Fact]
    public void Adjustment_RequiresExplicitDirection()
    {
        Assert.Throws<ArgumentException>(
            () => LedgerPolicy.MovementFor(TransactionType.Adjustment, 50m));
    }

    [Fact]
    public void Adjustment_UsesTheSuppliedSignedDeltas()
    {
        var movement = LedgerPolicy.MovementFor(
            TransactionType.Adjustment, 0m, explicitCashDelta: -25.50m, explicitFloatDelta: 0m);

        Assert.Equal(-25.50m, movement.CashDelta);
        Assert.Equal(0m, movement.FloatDelta);
    }

    [Fact]
    public void NegativeAmount_IsRejected()
    {
        // Direction belongs to the type. A negative magnitude would silently invert it.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LedgerPolicy.MovementFor(TransactionType.CashIn, -100m));
    }

    [Theory]
    [InlineData(TransactionType.CashIn, true)]
    [InlineData(TransactionType.CashOut, true)]
    [InlineData(TransactionType.Transfer, true)]
    [InlineData(TransactionType.Commission, true)]
    [InlineData(TransactionType.Unknown, false)]
    [InlineData(TransactionType.Reversal, false)]
    [InlineData(TransactionType.Adjustment, false)]
    public void OnlySafeTypesMayPostAutomatically(TransactionType type, bool expected)
    {
        Assert.Equal(expected, LedgerPolicy.CanPostAutomatically(type));
    }

    [Fact]
    public void EveryTransactionTypeHasAnExplicitRule()
    {
        // Guards against adding a type without deciding its accounting treatment.
        foreach (var type in Enum.GetValues<TransactionType>())
        {
            var movement = type switch
            {
                TransactionType.Adjustment => LedgerPolicy.MovementFor(type, 0m, explicitCashDelta: 0m),
                _ => LedgerPolicy.MovementFor(type, 1m)
            };

            Assert.False(string.IsNullOrWhiteSpace(movement.Rationale));
        }
    }

    [Fact]
    public void SmallestAmount_IsOnePesewa()
    {
        var movement = LedgerPolicy.MovementFor(TransactionType.CashIn, 0.01m);

        Assert.Equal(0.01m, movement.CashDelta);
        Assert.Equal(-0.01m, movement.FloatDelta);
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(0)]
    public void AmountsBelowOnePesewa_AreRejected(decimal amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LedgerPolicy.ValidateAmount(amount));
    }

    [Fact]
    public void AmountsBeyondStorageScale_AreRejected()
    {
        // numeric(18,4) would silently round a fifth decimal place away.
        Assert.Throws<ArgumentOutOfRangeException>(() => LedgerPolicy.ValidateAmount(1.234567m));
    }

    [Fact]
    public void RepeatedSmallAmounts_AccumulateExactly()
    {
        // The reason money is decimal and never double: with double, one hundred additions
        // of 0.01 does not equal 1.00.
        var total = 0m;
        for (var i = 0; i < 100; i++)
        {
            total += LedgerPolicy.MovementFor(TransactionType.CashIn, 0.01m).CashDelta;
        }

        Assert.Equal(1.00m, total);
    }
}

public class TransactionLifecycleTests
{
    [Theory]
    [InlineData(TransactionLifecycleState.Detected, false)]
    [InlineData(TransactionLifecycleState.Parsed, false)]
    [InlineData(TransactionLifecycleState.PendingReview, false)]
    [InlineData(TransactionLifecycleState.Rejected, false)]
    [InlineData(TransactionLifecycleState.Accepted, true)]
    [InlineData(TransactionLifecycleState.Synced, true)]
    [InlineData(TransactionLifecycleState.Reversed, true)]
    [InlineData(TransactionLifecycleState.Adjusted, true)]
    public void OnlyAcceptedStatesAffectTheLedger(TransactionLifecycleState state, bool expected)
    {
        Assert.Equal(expected, state.AffectsLedger());
    }

    [Fact]
    public void EvidenceStatesNeverAffectTheLedger()
    {
        // Detected and Parsed are observations, not accounting facts.
        Assert.False(TransactionLifecycleState.Detected.AffectsLedger());
        Assert.False(TransactionLifecycleState.Parsed.AffectsLedger());
    }

    [Fact]
    public void RejectedIsTerminal()
    {
        foreach (var target in Enum.GetValues<TransactionLifecycleState>())
        {
            if (target == TransactionLifecycleState.Rejected)
            {
                continue;
            }

            Assert.False(TransactionLifecycleState.Rejected.CanTransitionTo(target));
        }
    }

    [Fact]
    public void AcceptedCannotReturnToEvidenceStates()
    {
        Assert.False(TransactionLifecycleState.Accepted.CanTransitionTo(TransactionLifecycleState.Detected));
        Assert.False(TransactionLifecycleState.Accepted.CanTransitionTo(TransactionLifecycleState.Parsed));
        Assert.False(TransactionLifecycleState.Accepted.CanTransitionTo(TransactionLifecycleState.Rejected));
    }

    [Fact]
    public void AcceptedMayBeSyncedReversedOrAdjusted()
    {
        Assert.True(TransactionLifecycleState.Accepted.CanTransitionTo(TransactionLifecycleState.Synced));
        Assert.True(TransactionLifecycleState.Accepted.CanTransitionTo(TransactionLifecycleState.Reversed));
        Assert.True(TransactionLifecycleState.Accepted.CanTransitionTo(TransactionLifecycleState.Adjusted));
    }
}
