using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zazi.Application.Growth;
using Zazi.Application.Keypad;
using Zazi.Domain;

namespace Zazi.Infrastructure.Growth;

/// <summary>
/// A receipt texted to the customer for a cash in or cash out.
/// </summary>
/// <remarks>
/// The rival apps record for the agent; this gives the customer something too. A customer
/// with a receipt has proof of what happened and when, which ends most counter disputes before
/// they start — and the agent's name on it is a reason to come back. Only when the owner has
/// switched it on, since each receipt is an SMS the business pays for.
/// </remarks>
public sealed class CustomerReceipts : ICustomerReceipts
{
    private readonly ApplicationDbContext _db;
    private readonly ISmsSender? _sms;
    private readonly ILogger<CustomerReceipts>? _logger;

    public CustomerReceipts(ApplicationDbContext db, IEnumerable<ISmsSender>? sms = null, ILogger<CustomerReceipts>? logger = null)
    {
        _db = db;
        _sms = sms?.LastOrDefault();
        _logger = logger;
    }

    public async Task<bool> SendAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        if (_sms is null)
        {
            return false;
        }

        try
        {
            var t = await _db.Transactions.AsNoTracking()
                .Where(x => x.Id == transactionId)
                .Select(x => new { x.OrganizationId, x.Type, x.Amount, x.Network, x.CustomerPhoneNumber, x.TransactionAtUtc, x.Id })
                .SingleOrDefaultAsync(cancellationToken);
            if (t is null || t.Type is not (TransactionType.CashIn or TransactionType.CashOut))
            {
                return false;
            }

            if (GhanaPhoneNumber.Normalise(t.CustomerPhoneNumber) is not { } customer)
            {
                return false;
            }

            var org = await _db.Organizations.AsNoTracking()
                .Where(o => o.Id == t.OrganizationId)
                .Select(o => new { o.Name, o.PhoneNumber, o.SendCustomerReceipts })
                .SingleAsync(cancellationToken);
            if (!org.SendCustomerReceipts)
            {
                return false;
            }

            var message = Compose(org.Name, org.PhoneNumber, t.Type, t.Amount, t.Network, t.TransactionAtUtc, t.Id);
            return await _sms.SendAsync(customer, message, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "A customer receipt could not be sent.");
            return false;
        }
    }

    /// <summary>One SMS, from the customer's side of the counter.</summary>
    public static string Compose(string businessName, string? businessPhone, TransactionType type, decimal amount,
        string network, DateTimeOffset at, Guid id)
    {
        var what = type == TransactionType.CashOut ? "You withdrew" : "You deposited";
        var reference = id.ToString("N")[..6].ToUpperInvariant();
        var text = $"{SmsText.Plain(businessName, 24)} receipt: {what} GHS {amount:N2} ({NetworkName(network)}) " +
                   $"on {at:d MMM HH:mm}. Ref {reference}.";

        if (GhanaPhoneNumber.Normalise(businessPhone) is { } phone)
        {
            var withPhone = text + $" Questions: {GhanaPhoneNumber.Display(phone)}";
            if (withPhone.Length <= 160)
            {
                text = withPhone;
            }
        }

        return text;
    }

    private static string NetworkName(string network) => network.ToUpperInvariant() switch
    {
        "MTN" => "MTN",
        "TELECEL" => "Telecel",
        "AIRTELTIGO" => "AirtelTigo",
        _ => "MoMo"
    };
}
