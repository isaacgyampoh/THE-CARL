using TheCarl.Domain;
using TheCarl.Infrastructure;

namespace TheCarl.IntegrationTests;

/// <summary>A seeded organization with two branches and a device, for isolation tests.</summary>
internal sealed record TestTenant(
    Guid OrganizationId,
    Guid BranchAId,
    Guid BranchBId,
    Guid DeviceId,
    Guid OwnerUserId,
    Guid AgentUserId);

internal static class TenantSeeder
{
    public static async Task<TestTenant> SeedAsync(CarlApiFactory factory, string name)
    {
        return await factory.WithDbAsync(async db =>
        {
            var organization = new Organization { Name = name, Country = "GH", CurrencyCode = Money.DefaultCurrency };
            var branchA = new Branch { OrganizationId = organization.Id, Name = $"{name} Branch A" };
            var branchB = new Branch { OrganizationId = organization.Id, Name = $"{name} Branch B" };

            var device = new Device
            {
                OrganizationId = organization.Id,
                BranchId = branchA.Id,
                Name = $"{name} POS",
                DeviceIdentifier = $"{name}-device-1",
                Network = "MTN"
            };

            var owner = new User
            {
                OrganizationId = organization.Id,
                FullName = $"{name} Owner",
                Email = $"owner@{name.ToLowerInvariant()}.test",
                IsActive = true
            };

            var agent = new User
            {
                OrganizationId = organization.Id,
                BranchId = branchA.Id,
                FullName = $"{name} Agent",
                Email = $"agent@{name.ToLowerInvariant()}.test",
                IsActive = true
            };

            db.Organizations.Add(organization);
            db.Branches.AddRange(branchA, branchB);
            db.Devices.Add(device);
            db.Users.AddRange(owner, agent);
            await db.SaveChangesAsync();

            return new TestTenant(
                organization.Id,
                branchA.Id,
                branchB.Id,
                device.Id,
                owner.Id,
                agent.Id);
        });
    }
}
