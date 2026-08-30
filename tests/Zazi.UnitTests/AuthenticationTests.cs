using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Domain;
using Zazi.Infrastructure;

namespace Zazi.UnitTests;

/// <summary>Credential handling, token rotation and account-lockout behaviour.</summary>
public class AuthenticationTests
{
    [Fact]
    public async Task SameEmailInTwoOrganizations_ResolvesToTheCorrectUser()
    {
        // Email is unique per organization, not globally. A bare email lookup previously
        // used SingleOrDefault and threw once two tenants shared an address, making login
        // fail for both.
        await using var db = TestServices.CreateDbContext();
        var (orgA, branchA) = await SeedOrganizationAsync(db, "Shared A");
        var (orgB, branchB) = await SeedOrganizationAsync(db, "Shared B");

        var auth = TestServices.CreateAuthService(db);

        await auth.RegisterUserAsync(new RegisterUserRequest(
            orgA.Id, branchA.Id, "Ama Mensah", "shared@carl.test", "Alpha-Passphrase-1", null, [ZaziRoles.Agent]));
        await auth.RegisterUserAsync(new RegisterUserRequest(
            orgB.Id, branchB.Id, "Kofi Boateng", "shared@carl.test", "Beta-Passphrase-2", null, [ZaziRoles.Agent]));

        var loginA = await auth.LoginAsync(new LoginRequest("shared@carl.test", "Alpha-Passphrase-1"));
        var loginB = await auth.LoginAsync(new LoginRequest("shared@carl.test", "Beta-Passphrase-2"));

        Assert.Equal(orgA.Id, loginA.User.OrganizationId);
        Assert.Equal("Ama Mensah", loginA.User.FullName);
        Assert.Equal(orgB.Id, loginB.User.OrganizationId);
        Assert.Equal("Kofi Boateng", loginB.User.FullName);
    }

    [Fact]
    public async Task RefreshTokensAreNotStoredInClearText()
    {
        await using var db = TestServices.CreateDbContext();
        var (org, branch) = await SeedOrganizationAsync(db, "Hashing");
        var auth = TestServices.CreateAuthService(db);

        await auth.RegisterUserAsync(new RegisterUserRequest(
            org.Id, branch.Id, "Yaa Asantewaa", "yaa@carl.test", "Str0ng-Passphrase!", null, [ZaziRoles.Agent]));
        var login = await auth.LoginAsync(new LoginRequest("yaa@carl.test", "Str0ng-Passphrase!"));

        var stored = await db.RefreshTokens.SingleAsync();

        Assert.NotEqual(login.RefreshToken, stored.TokenHash);
        Assert.DoesNotContain(login.RefreshToken, stored.TokenHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotatedRefreshTokenCannotBeUsedTwice()
    {
        await using var db = TestServices.CreateDbContext();
        var (org, branch) = await SeedOrganizationAsync(db, "Rotation");
        var auth = TestServices.CreateAuthService(db);

        await auth.RegisterUserAsync(new RegisterUserRequest(
            org.Id, branch.Id, "Kwame Nkrumah", "kwame@carl.test", "Str0ng-Passphrase!", null, [ZaziRoles.Agent]));
        var login = await auth.LoginAsync(new LoginRequest("kwame@carl.test", "Str0ng-Passphrase!"));

        var rotated = await auth.RefreshTokenAsync(new RefreshTokenRequest(login.RefreshToken));

        Assert.NotEqual(login.RefreshToken, rotated.RefreshToken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => auth.RefreshTokenAsync(new RefreshTokenRequest(login.RefreshToken)));
    }

    [Fact]
    public async Task ReusingARotatedTokenRevokesTheWholeFamily()
    {
        // Reuse means the token leaked. The replacement the legitimate client holds must
        // stop working too, so the theft cannot go unnoticed.
        await using var db = TestServices.CreateDbContext();
        var (org, branch) = await SeedOrganizationAsync(db, "Reuse");
        var auth = TestServices.CreateAuthService(db);

        await auth.RegisterUserAsync(new RegisterUserRequest(
            org.Id, branch.Id, "Efua Sutherland", "efua@carl.test", "Str0ng-Passphrase!", null, [ZaziRoles.Agent]));
        var login = await auth.LoginAsync(new LoginRequest("efua@carl.test", "Str0ng-Passphrase!"));
        var rotated = await auth.RefreshTokenAsync(new RefreshTokenRequest(login.RefreshToken));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => auth.RefreshTokenAsync(new RefreshTokenRequest(login.RefreshToken)));

        // The replacement token is now dead as well.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => auth.RefreshTokenAsync(new RefreshTokenRequest(rotated.RefreshToken)));

        Assert.True(await db.RefreshTokens.AllAsync(t => t.RevokedAtUtc != null));
    }

    [Fact]
    public async Task RefreshTokenCannotBeRedeemedAgainstAnotherOrganization()
    {
        await using var db = TestServices.CreateDbContext();
        var (org, branch) = await SeedOrganizationAsync(db, "OrgBinding");
        var auth = TestServices.CreateAuthService(db);

        await auth.RegisterUserAsync(new RegisterUserRequest(
            org.Id, branch.Id, "Nii Lamptey", "nii@carl.test", "Str0ng-Passphrase!", null, [ZaziRoles.Agent]));
        var login = await auth.LoginAsync(new LoginRequest("nii@carl.test", "Str0ng-Passphrase!"));

        await Assert.ThrowsAsync<TenantAccessDeniedException>(
            () => auth.RefreshTokenAsync(new RefreshTokenRequest(login.RefreshToken, Guid.NewGuid())));
    }

    [Fact]
    public async Task RepeatedFailedLoginsLockTheAccountAndRaiseAnAlert()
    {
        await using var db = TestServices.CreateDbContext();
        var (org, branch) = await SeedOrganizationAsync(db, "Lockout");
        var auth = TestServices.CreateAuthService(db);

        await auth.RegisterUserAsync(new RegisterUserRequest(
            org.Id, branch.Id, "Abena Osei", "abena@carl.test", "Str0ng-Passphrase!", null, [ZaziRoles.Agent]));

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => auth.LoginAsync(new LoginRequest("abena@carl.test", "wrong-password-value")));
        }

        // Correct credentials are now refused because the account is locked.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => auth.LoginAsync(new LoginRequest("abena@carl.test", "Str0ng-Passphrase!")));

        Assert.True(await db.Alerts.AnyAsync(a => a.Type == "MULTIPLE_FAILED_LOGINS"));
    }

    [Theory]
    [InlineData("short1!A")]           // under 12 characters
    [InlineData("alllowercaseonly")]   // single character class
    [InlineData("")]
    public async Task WeakPasswordsAreRejected(string password)
    {
        await using var db = TestServices.CreateDbContext();
        var (org, branch) = await SeedOrganizationAsync(db, "Strength");
        var auth = TestServices.CreateAuthService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => auth.RegisterUserAsync(new RegisterUserRequest(
            org.Id, branch.Id, "Weak Password", "weak@carl.test", password, null, [ZaziRoles.Agent])));
    }

    [Fact]
    public async Task UnknownRoleNamesAreRejectedRatherThanDefaulted()
    {
        await using var db = TestServices.CreateDbContext();
        var (org, branch) = await SeedOrganizationAsync(db, "RoleTypo");
        var auth = TestServices.CreateAuthService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => auth.RegisterUserAsync(new RegisterUserRequest(
            org.Id, branch.Id, "Typo Role", "typo@carl.test", "Str0ng-Passphrase!", null, ["SUPER_OWNER"])));
    }

    [Fact]
    public async Task LegacyRoleNamesMapOntoCanonicalRoles()
    {
        await using var db = TestServices.CreateDbContext();
        var (org, _) = await SeedOrganizationAsync(db, "Legacy");
        var auth = TestServices.CreateAuthService(db);

        var user = await auth.RegisterUserAsync(new RegisterUserRequest(
            org.Id, null, "Legacy Owner", "legacy@carl.test", "Str0ng-Passphrase!", null, ["OrganizationOwner"]));

        Assert.Contains(ZaziRoles.Owner, user.Roles);
    }

    [Fact]
    public async Task OneRoleCanBeSharedByManyUsersInTheSameOrganization()
    {
        // The previous mapping made Role.OrganizationId a foreign key to User.Id, so
        // assigning an existing role to a second user rewrote the role's owner.
        await using var db = TestServices.CreateDbContext();
        var (org, branch) = await SeedOrganizationAsync(db, "SharedRole");
        var auth = TestServices.CreateAuthService(db);

        var first = await auth.RegisterUserAsync(new RegisterUserRequest(
            org.Id, branch.Id, "First Agent", "first@carl.test", "Str0ng-Passphrase!", null, [ZaziRoles.Agent]));
        var second = await auth.RegisterUserAsync(new RegisterUserRequest(
            org.Id, branch.Id, "Second Agent", "second@carl.test", "Str0ng-Passphrase!", null, [ZaziRoles.Agent]));

        Assert.Contains(ZaziRoles.Agent, first.Roles);
        Assert.Contains(ZaziRoles.Agent, second.Roles);

        var reloaded = await auth.GetUsersAsync(org.Id);
        Assert.All(reloaded, u => Assert.Contains(ZaziRoles.Agent, u.Roles));
        Assert.Equal(1, await db.Roles.CountAsync(r => r.OrganizationId == org.Id && r.Name == ZaziRoles.Agent));
    }

    [Fact]
    public async Task BranchScopedRoleRequiresABranch()
    {
        await using var db = TestServices.CreateDbContext();
        var (org, _) = await SeedOrganizationAsync(db, "NeedBranch");
        var auth = TestServices.CreateAuthService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => auth.RegisterUserAsync(new RegisterUserRequest(
            org.Id, null, "Floating Agent", "floating@carl.test", "Str0ng-Passphrase!", null, [ZaziRoles.Agent])));
    }

    [Fact]
    public async Task StaffCannotBeAttachedToAnotherOrganizationsBranch()
    {
        await using var db = TestServices.CreateDbContext();
        var (orgA, _) = await SeedOrganizationAsync(db, "Owner Org");
        var (_, foreignBranch) = await SeedOrganizationAsync(db, "Foreign Org");
        var auth = TestServices.CreateAuthService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => auth.RegisterUserAsync(new RegisterUserRequest(
            orgA.Id, foreignBranch.Id, "Cross Tenant", "cross@carl.test", "Str0ng-Passphrase!", null, [ZaziRoles.Agent])));
    }

    private static async Task<(Organization Organization, Branch Branch)> SeedOrganizationAsync(
        ApplicationDbContext db,
        string name)
    {
        var organization = new Organization { Name = name, Country = "GH", CurrencyCode = Money.DefaultCurrency };
        var branch = new Branch { OrganizationId = organization.Id, Name = $"{name} Main" };
        db.Organizations.Add(organization);
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        return (organization, branch);
    }
}
