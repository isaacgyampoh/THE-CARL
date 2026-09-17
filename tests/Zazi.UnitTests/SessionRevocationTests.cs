using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Domain;
using Zazi.Infrastructure;
using Zazi.Infrastructure.Services;

namespace Zazi.UnitTests;

/// <summary>
/// Session revocation and device binding: a session must die the moment its user, its
/// device, or its security stamp becomes untrustworthy — without waiting for expiry.
/// </summary>
public class SessionRevocationTests
{
    [Fact]
    public async Task LoginBindsTheSessionToTheSuppliedDevice()
    {
        await using var db = TestServices.CreateDbContext();
        var context = await SeedAsync(db);

        var login = await context.Auth.LoginAsync(
            new LoginRequest(context.Email, context.Password, context.DeviceIdentifier));

        var session = await db.AuthSessions.SingleAsync();

        Assert.Equal(context.DeviceId, session.DeviceId);
        Assert.Equal(AuthSessionStatus.Active, session.Status);
        Assert.False(string.IsNullOrWhiteSpace(login.RefreshToken));

        var token = await db.RefreshTokens.SingleAsync();
        Assert.Equal(session.Id, token.SessionId);
        Assert.Equal(session.FamilyId, token.FamilyId);
    }

    [Fact]
    public async Task ARevokedDeviceCannotSignIn()
    {
        await using var db = TestServices.CreateDbContext();
        var context = await SeedAsync(db);

        var device = await db.Devices.SingleAsync(x => x.Id == context.DeviceId);
        device.Status = DeviceStatus.Revoked;
        device.IsRevoked = true;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Auth.LoginAsync(new LoginRequest(context.Email, context.Password, context.DeviceIdentifier)));
    }

    [Fact]
    public async Task ARevokedDeviceCannotRefresh()
    {
        await using var db = TestServices.CreateDbContext();
        var context = await SeedAsync(db);

        var login = await context.Auth.LoginAsync(
            new LoginRequest(context.Email, context.Password, context.DeviceIdentifier));

        // The handset is decommissioned after the session was already established.
        var device = await db.Devices.SingleAsync(x => x.Id == context.DeviceId);
        device.Status = DeviceStatus.Revoked;
        device.IsRevoked = true;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Auth.RefreshTokenAsync(new RefreshTokenRequest(login.RefreshToken)));
    }

    [Fact]
    public async Task RevokingADeviceClosesItsSessions()
    {
        await using var db = TestServices.CreateDbContext();
        var context = await SeedAsync(db);

        await context.Auth.LoginAsync(new LoginRequest(context.Email, context.Password, context.DeviceIdentifier));

        var revocation = new IdentityRevocationService(db);
        var closed = await revocation.RevokeDeviceSessionsAsync(context.DeviceId);

        Assert.Equal(1, closed);
        var session = await db.AuthSessions.SingleAsync();
        Assert.Equal(AuthSessionStatus.RevokedByDeviceRevocation, session.Status);
        Assert.True(await db.RefreshTokens.AllAsync(t => t.RevokedAtUtc != null));
    }

    [Fact]
    public async Task ASecurityStampChangeInvalidatesRefreshImmediately()
    {
        await using var db = TestServices.CreateDbContext();
        var context = await SeedAsync(db);

        var login = await context.Auth.LoginAsync(
            new LoginRequest(context.Email, context.Password, context.DeviceIdentifier));

        // Simulates a password change, a role change, or an administrator revoking sessions.
        var revocation = new IdentityRevocationService(db);
        await revocation.RevokeAllSessionsAsync(context.UserId, RevocationTrigger.PasswordChanged);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Auth.RefreshTokenAsync(new RefreshTokenRequest(login.RefreshToken)));
    }

    [Fact]
    public async Task RevokeAllSessionsRotatesTheSecurityStamp()
    {
        await using var db = TestServices.CreateDbContext();
        var context = await SeedAsync(db);

        var before = (await db.Users.SingleAsync(x => x.Id == context.UserId)).SecurityStamp;

        var revocation = new IdentityRevocationService(db);
        await revocation.RevokeAllSessionsAsync(context.UserId, RevocationTrigger.AdministratorRevoked);

        var after = (await db.Users.AsNoTracking().SingleAsync(x => x.Id == context.UserId)).SecurityStamp;

        // Rotating the stamp is what invalidates sessions created concurrently with the
        // revocation, which a query over existing rows alone would miss.
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task RevokingOneSessionLeavesOtherSessionsWorking()
    {
        await using var db = TestServices.CreateDbContext();
        var context = await SeedAsync(db);

        var phoneLogin = await context.Auth.LoginAsync(
            new LoginRequest(context.Email, context.Password, context.DeviceIdentifier));
        var webLogin = await context.Auth.LoginAsync(
            new LoginRequest(context.Email, context.Password));

        var phoneSession = await db.AuthSessions.SingleAsync(x => x.DeviceId == context.DeviceId);

        var revocation = new IdentityRevocationService(db);
        Assert.True(await revocation.RevokeSessionAsync(phoneSession.Id, RevocationTrigger.AdministratorRevoked));

        // The revoked session is dead...
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Auth.RefreshTokenAsync(new RefreshTokenRequest(phoneLogin.RefreshToken)));

        // ...while the untouched one still refreshes, because the stamp was not rotated.
        var refreshed = await context.Auth.RefreshTokenAsync(new RefreshTokenRequest(webLogin.RefreshToken));
        Assert.False(string.IsNullOrWhiteSpace(refreshed.AccessToken));
    }

    [Fact]
    public async Task AnUnregisteredDeviceIdentifierIsRefused()
    {
        await using var db = TestServices.CreateDbContext();
        var context = await SeedAsync(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Auth.LoginAsync(new LoginRequest(context.Email, context.Password, "never-registered")));
    }

    private sealed record SeedContext(
        AuthService Auth,
        Guid UserId,
        Guid DeviceId,
        string DeviceIdentifier,
        string Email,
        string Password);

    private static async Task<SeedContext> SeedAsync(ApplicationDbContext db)
    {
        const string email = "session.user@carl.test";
        const string password = "Str0ng-Passphrase!";
        const string deviceIdentifier = "zazi-device-0001";

        var organization = new Organization { Name = "Session Org", Country = "GH", CurrencyCode = Money.DefaultCurrency };
        var branch = new Branch { OrganizationId = organization.Id, Name = "Main" };
        var device = new Device
        {
            OrganizationId = organization.Id,
            BranchId = branch.Id,
            Name = "Agent Phone",
            DeviceIdentifier = deviceIdentifier,
            Status = DeviceStatus.Active
        };

        db.Organizations.Add(organization);
        db.Branches.Add(branch);
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        var auth = TestServices.CreateAuthService(db);
        var user = await auth.RegisterUserAsync(new RegisterUserRequest(
            organization.Id, branch.Id, "Session User", email, password, null, [ZaziRoles.Agent]));

        return new SeedContext(auth, user.Id, device.Id, deviceIdentifier, email, password);
    }
}
