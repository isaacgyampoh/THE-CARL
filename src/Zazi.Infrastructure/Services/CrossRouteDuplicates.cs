using Microsoft.EntityFrameworkCore;
using Zazi.Domain;

namespace Zazi.Infrastructure.Services;

/// <summary>
/// One real MoMo transaction, recorded once — however many routes it arrives by.
/// </summary>
/// <remarks>
/// <para>
/// A transaction can reach Zazi by the handset reading the SMS, by an agent forwarding it from a
/// keypad phone, or by the owner's portal. Each route already refuses its own repeats, but by its
/// own fingerprint, and the fingerprints differ: the handset stamps the moment it saw the
/// message, a forward carries the moment it was forwarded. So the same cash-out could pass both
/// checks and be counted twice.
/// </para>
/// <para>
/// What every route shares for a real transaction is the network's own transaction ID. Two
/// records with the same network, the same ID, the same direction and the same amount are the
/// same event. A reversal is left out on purpose: its message quotes the ID of the transaction it
/// undoes, and treating it as a repeat of that transaction would lose the reversal.
/// </para>
/// </remarks>
public static class CrossRouteDuplicates
{
    /// <summary>Shorter than this and a "reference" is more likely a fragment than an ID.</summary>
    private const int MinimumReferenceLength = 6;

    private static readonly TransactionType[] Checked =
    [
        TransactionType.CashIn,
        TransactionType.CashOut,
        TransactionType.Commission,
        TransactionType.Transfer
    ];

    /// <summary>The existing transaction this one repeats, or null.</summary>
    public static async Task<Guid?> FindAsync(
        ApplicationDbContext db,
        Guid organizationId,
        string? network,
        TransactionType type,
        decimal amount,
        string? providerReference,
        CancellationToken cancellationToken)
    {
        var asGiven = providerReference?.Trim();
        var reference = asGiven?.ToUpperInvariant();
        if (reference is null || reference.Length < MinimumReferenceLength || !Checked.Contains(type))
        {
            return null;
        }

        var normalisedNetwork = Networks.Normalise(network);
        if (normalisedNetwork == Networks.Unspecified)
        {
            return null;
        }

        var match = await db.Transactions.AsNoTracking()
            .Where(t => t.OrganizationId == organizationId
                // Equality rather than ToUpper() on the column, so the (organisation, reference)
                // index answers it. Network IDs are digits or upper case in practice; both
                // spellings of what arrived are tried.
                && (t.ProviderReference == asGiven || t.ProviderReference == reference)
                && t.Network.ToUpper() == normalisedNetwork
                && t.Type == type
                && t.Amount == amount)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return match;
    }
}
