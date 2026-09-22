using Microsoft.EntityFrameworkCore;
using Zazi.Application.Growth;
using Zazi.Domain;

namespace Zazi.Infrastructure.Growth;

public sealed class BusinessSettingsService : IBusinessSettingsService
{
    private readonly ApplicationDbContext _db;

    public BusinessSettingsService(ApplicationDbContext db) => _db = db;

    public async Task<BusinessSettings> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var org = await _db.Organizations.AsNoTracking()
            .SingleAsync(o => o.Id == organizationId, cancellationToken);
        return new BusinessSettings(org.Name, GhanaPhoneNumber.Normalise(org.PhoneNumber), org.SendCustomerReceipts,
            org.SendDailyDigest);
    }

    public async Task SaveAsync(Guid organizationId, string? smsPhoneNumber, bool sendCustomerReceipts,
        bool sendDailyDigest, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        string? number = null;
        if (!string.IsNullOrWhiteSpace(smsPhoneNumber))
        {
            number = GhanaPhoneNumber.Normalise(smsPhoneNumber)
                ?? throw new ArgumentException("Enter a Ghanaian mobile number, such as 024 412 3456.");
        }

        var org = await _db.Organizations.SingleAsync(o => o.Id == organizationId, cancellationToken);
        var changes = new List<string>();
        if (!string.Equals(GhanaPhoneNumber.Normalise(org.PhoneNumber), number, StringComparison.Ordinal))
        {
            changes.Add(number is null ? "SMS number removed" : "SMS number changed");
        }
        if (org.SendCustomerReceipts != sendCustomerReceipts)
        {
            changes.Add(sendCustomerReceipts ? "customer receipts switched on" : "customer receipts switched off");
        }
        if (org.SendDailyDigest != sendDailyDigest)
        {
            changes.Add(sendDailyDigest ? "evening email switched on" : "evening email switched off");
        }

        // Stored normalised, so an owner's reply by SMS can be recognised by its number.
        org.PhoneNumber = number;
        org.SendCustomerReceipts = sendCustomerReceipts;
        org.SendDailyDigest = sendDailyDigest;
        org.UpdatedAt = DateTimeOffset.UtcNow;

        if (changes.Count > 0)
        {
            _db.AuditLogs.Add(new AuditLogEntry
            {
                OrganizationId = organizationId,
                UserId = actorUserId,
                Action = "BUSINESS_SETTINGS_CHANGED",
                Details = string.Join("; ", changes) + ".",
                ActorType = "User"
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
