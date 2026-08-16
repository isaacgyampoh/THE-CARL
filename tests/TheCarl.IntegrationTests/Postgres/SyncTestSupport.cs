using TheCarl.Domain;

namespace TheCarl.IntegrationTests.Postgres;

/// <summary>A seeded tenant with one branch, one agent and one device.</summary>
internal sealed record TenantSeed(Guid OrganizationId, Guid BranchId, Guid UserId, Guid DeviceId);

internal static class TenantSeedFactory
{
    public static async Task<TenantSeed> CreateAsync(PostgresFixture postgres)
    {
        await using var db = postgres.CreateContext();

        var organization = new Organization
        {
            Name = $"Tenant {Guid.NewGuid():N}",
            Country = "GH",
            CurrencyCode = Money.DefaultCurrency
        };
        var branch = new Branch { OrganizationId = organization.Id, Name = "Main" };
        var user = new User
        {
            OrganizationId = organization.Id,
            BranchId = branch.Id,
            FullName = "Test Agent",
            Email = $"agent-{Guid.NewGuid():N}@carl.test",
            IsActive = true
        };
        var device = new Device
        {
            OrganizationId = organization.Id,
            BranchId = branch.Id,
            Name = "Agent Phone",
            DeviceIdentifier = $"device-{Guid.NewGuid():N}",
            Status = DeviceStatus.Active
        };

        db.Organizations.Add(organization);
        db.Branches.Add(branch);
        db.Users.Add(user);
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        return new TenantSeed(organization.Id, branch.Id, user.Id, device.Id);
    }
}

/// <summary>
/// Builds sync request items as anonymous objects so the tests exercise real JSON binding
/// rather than sharing the server's own DTO types.
/// </summary>
internal static class SyncItemFactory
{
    public static object CashIn(Guid branchId, decimal amount = 100m, DateTimeOffset? occurredAt = null) =>
        new
        {
            clientTransactionId = ClientTransactionId.Create($"device-{branchId:N}"),
            transactionType = (int)TransactionType.CashIn,
            amount,
            provider = "MTN",
            transactionTimestamp = occurredAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            deviceReceivedAt = occurredAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            branchId,
            sourceType = (int)EvidenceSourceType.ManualEntry,
            parserVersion = "manual-v1",
            // Unique by default so distinct test transactions do not collide on the
            // organization-scoped evidence fingerprint.
            transactionReference = Guid.NewGuid().ToString("N")[..12]
        };

    public static object Reversal(Guid branchId, Guid originalId, decimal amount) =>
        new
        {
            clientTransactionId = ClientTransactionId.Create($"device-{branchId:N}"),
            transactionType = (int)TransactionType.Reversal,
            amount,
            provider = "MTN",
            transactionTimestamp = DateTimeOffset.UtcNow.AddMinutes(-1),
            deviceReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            branchId,
            sourceType = (int)EvidenceSourceType.ManualEntry,
            parserVersion = "manual-v1",
            reversesTransactionId = originalId,
            correctionReason = "Customer cancelled",
            transactionReference = Guid.NewGuid().ToString("N")[..12]
        };
}
