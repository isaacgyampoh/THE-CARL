using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Domain;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// Worker activation over real HTTP against real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// Activation is the only anonymous endpoint in the product. It takes a code from a caller
/// with no identity and returns a session that can read a business's financial records, so
/// every one of its refusals is load-bearing and each is asserted here rather than assumed
/// from the shared code path with enrolment.
/// </para>
/// <para>
/// The most important tests in this file are the two credential-bypass ones. Making
/// <c>Email</c> nullable and creating users with no password is only safe because two
/// independent guards refuse them at login, and each is proved on its own so that removing
/// either would fail a test.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class WorkerActivationTests : IDisposable
{
    private const string CodesPath = "/api/v1/devices/enrollment-codes";
    private const string WorkersPath = "/api/v1/auth/workers";
    private const string ActivatePath = "/api/v1/devices/activate";
    private const string LoginPath = "/api/v1/auth/login";

    private readonly PostgresFixture _postgres;
    private readonly PostgresApiFactory? _factory;

    public WorkerActivationTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = postgres.IsAvailable ? new PostgresApiFactory(postgres.ConnectionString!) : null;
    }

    public void Dispose() => _factory?.Dispose();

    // ─── The happy path ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AWorkerActivatesWithNoAccountAndReceivesAWorkingSession()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Ama Mensah");
        var issued = await IssueAsync(tenant, worker.Id);

        var result = await ActivateAsync(issued.Code, "handset-" + Guid.NewGuid().ToString("N"));

        // The worker is told who the server thinks they are, from real data.
        Assert.Equal("Ama Mensah", result.WorkerName);
        Assert.Equal(tenant.BranchId, result.BranchId);
        Assert.False(string.IsNullOrWhiteSpace(result.BranchName));
        Assert.False(string.IsNullOrWhiteSpace(result.OrganizationName));

        // And the session is a real one, not a placeholder.
        Assert.False(string.IsNullOrWhiteSpace(result.Session.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(result.Session.RefreshToken));
    }

    [SkippableFact]
    public async Task TheIssuedSessionIsAcceptedByProtectedEndpoints()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Kofi Boateng");
        var issued = await IssueAsync(tenant, worker.Id);

        var result = await ActivateAsync(issued.Code, "handset-" + Guid.NewGuid().ToString("N"));

        // A session that cannot reach the API is not a session. This is what proves
        // activation produced the same thing login produces.
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", result.Session.AccessToken);

        var response = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task ActivationBindsTheDeviceToTheBranchAndRoleFromTheCode()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Yaa Asantewaa");
        var issued = await IssueAsync(tenant, worker.Id);
        var identifier = "handset-" + Guid.NewGuid().ToString("N");

        var result = await ActivateAsync(issued.Code, identifier);

        await using var db = _postgres.CreateContext();
        var device = await db.Devices.AsNoTracking().SingleAsync(x => x.Id == result.DeviceId);

        Assert.Equal(tenant.OrganizationId, device.OrganizationId);
        Assert.Equal(tenant.BranchId, device.BranchId);
        Assert.Equal(DeviceStatus.Active, device.Status);
        Assert.Equal(identifier, device.DeviceIdentifier);
    }

    // ─── The client decides nothing ──────────────────────────────────────────

    [SkippableFact]
    public async Task AClientCannotChooseItsOwnOrganizationOrBranch()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var intruder = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Scoped Worker");
        var issued = await IssueAsync(tenant, worker.Id);

        // Extra fields naming another tenant. They are not on the contract, so they are
        // ignored — this asserts that adding them changes nothing.
        var response = await _factory!.CreateClient().PostAsJsonAsync(ActivatePath, new
        {
            code = issued.Code,
            deviceIdentifier = "handset-" + Guid.NewGuid().ToString("N"),
            organizationId = intruder.OrganizationId,
            branchId = intruder.BranchId,
            role = "OWNER"
        });

        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<DeviceActivationResult>())!;

        await using var db = _postgres.CreateContext();
        var device = await db.Devices.AsNoTracking().SingleAsync(x => x.Id == result.DeviceId);

        // Scope came from the code, not from the request.
        Assert.Equal(tenant.OrganizationId, device.OrganizationId);
        Assert.Equal(tenant.BranchId, device.BranchId);
        Assert.NotEqual(intruder.OrganizationId, device.OrganizationId);
    }

    // ─── Refusals ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnUnrecognisedCodeIsRefused()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var response = await RawActivateAsync("ZAZI-ZZZZ-ZZZZ-ZZZZ-ZZZZ", "handset-x");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task ACodeWithNoWorkerBoundCannotActivate()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        // Valid in every other respect, but carries no identity — so there is nobody to
        // issue a session to. It remains usable on the authenticated enrolment path.
        var issued = await IssueAsync(tenant, intendedUserId: null);

        var response = await RawActivateAsync(issued.Code, "handset-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task AnExpiredCodeIsRefused()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Expired Worker");
        var issued = await IssueAsync(tenant, worker.Id);

        // Aged rather than given a past expiry: the schema enforces ExpiresAtUtc > CreatedAt,
        // so backdating only the expiry is not a state the database will accept — and that
        // constraint is what stops a code outliving the worker it was issued for.
        await MutateCodeAsync(issued.Id, c =>
        {
            c.CreatedAt = DateTimeOffset.UtcNow.AddHours(-48);
            c.ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(-24);
        });

        var response = await RawActivateAsync(issued.Code, "handset-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task ARevokedCodeIsRefused()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Revoked Worker");
        var issued = await IssueAsync(tenant, worker.Id);

        // The timestamp is not optional decoration — a check constraint requires a terminal
        // state to record when it was reached.
        await MutateCodeAsync(issued.Id, c =>
        {
            c.Status = DeviceEnrollmentCodeStatus.Revoked;
            c.RevokedAtUtc = DateTimeOffset.UtcNow;
        });

        var response = await RawActivateAsync(issued.Code, "handset-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task ACodeCannotBeUsedTwice()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Single Use");
        var issued = await IssueAsync(tenant, worker.Id);

        await ActivateAsync(issued.Code, "handset-" + Guid.NewGuid().ToString("N"));

        // A second handset, a second identifier — the code is what is spent.
        var response = await RawActivateAsync(issued.Code, "handset-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task ACodeIsRefusedOnceTheAttemptLimitIsReached()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Brute Forced");
        var issued = await IssueAsync(tenant, worker.Id);

        await MutateCodeAsync(issued.Id, c => c.FailedAttempts = DeviceEnrollmentCode.MaxFailedAttempts);

        // The code itself is correct. It is refused because too much has been thrown at it.
        var response = await RawActivateAsync(issued.Code, "handset-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task ADisabledWorkerCannotActivate()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Dismissed");
        var issued = await IssueAsync(tenant, worker.Id);

        await using (var db = _postgres.CreateContext())
        {
            var entity = await db.Users.SingleAsync(x => x.Id == worker.Id);
            entity.IsActive = false;
            await db.SaveChangesAsync();
        }

        // The owner disabled them after issuing the code. The code must not outlive them.
        var response = await RawActivateAsync(issued.Code, "handset-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task AnAlreadyRegisteredDeviceIdentifierIsRefused()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var first = await CreateWorkerAsync(tenant, "First");
        var second = await CreateWorkerAsync(tenant, "Second");
        var identifier = "handset-" + Guid.NewGuid().ToString("N");

        await ActivateAsync((await IssueAsync(tenant, first.Id)).Code, identifier);

        // Re-activating a known handset would re-admit a revoked one and make revocation a
        // formality.
        var response = await RawActivateAsync((await IssueAsync(tenant, second.Id)).Code, identifier);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ─── Concurrency ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TwoHandsetsRacingOnOneCodeProduceExactlyOneDevice()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Raced");
        var issued = await IssueAsync(tenant, worker.Id);

        // Different identifiers, so no unique index on the device can decide this. Only the
        // conditional claim on the code row can.
        var first = RawActivateAsync(issued.Code, "handset-a-" + Guid.NewGuid().ToString("N"));
        var second = RawActivateAsync(issued.Code, "handset-b-" + Guid.NewGuid().ToString("N"));

        var responses = await Task.WhenAll(first, second);

        Assert.Equal(1, responses.Count(r => r.IsSuccessStatusCode));

        await using var db = _postgres.CreateContext();
        var devices = await db.Devices.AsNoTracking()
            .CountAsync(x => x.OrganizationId == tenant.OrganizationId && x.BranchId == tenant.BranchId);

        // One from the seed, one from the single winning activation.
        Assert.Equal(2, devices);
    }

    // ─── Credential bypass: the two guards, proved independently ─────────────

    [SkippableFact]
    public async Task MarkingAnAccountActivationOnlyStopsItSigningIn()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        // A real account with a real password, created through the real registration path so
        // the credential is hashed by the production hasher rather than reproduced here.
        const string password = "Sup3rSecret!Passw0rd";
        var email = $"staff-{Guid.NewGuid():N}@example.com";

        var created = await ManagerClient(tenant).PostAsJsonAsync("/api/v1/auth/staff", new
        {
            branchId = tenant.BranchId,
            fullName = "Convertible Staff",
            email,
            password,
            roles = new[] { ZaziRoles.Agent }
        });
        created.EnsureSuccessStatusCode();

        // Baseline: the credential genuinely works. Without this the test below could pass
        // for the wrong reason — a password that never worked proves nothing.
        var before = await _factory!.CreateClient().PostAsJsonAsync(LoginPath, new { email, password });
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        await using (var db = _postgres.CreateContext())
        {
            var entity = await db.Users.SingleAsync(x => x.Email == email);
            entity.CredentialType = UserCredentialType.ActivationOnly;
            await db.SaveChangesAsync();
        }

        // Nothing changed but the flag, and the same credential is now refused. That isolates
        // the CredentialType filter from the empty-hash guard entirely.
        var after = await _factory!.CreateClient().PostAsJsonAsync(LoginPath, new { email, password });
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [SkippableFact]
    public async Task APasswordAccountWithAnEmptyHashCannotSignIn()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Empty Hash");

        // Marked as a password account, so the CredentialType filter lets it through to the
        // verifier. Only VerifyPassword's refusal of a blank hash can reject it — the second
        // guard, proved without the first.
        string email;
        await using (var db = _postgres.CreateContext())
        {
            var entity = await db.Users.SingleAsync(x => x.Id == worker.Id);
            email = $"empty-{Guid.NewGuid():N}@example.com";
            entity.Email = email;
            entity.CredentialType = UserCredentialType.Password;
            entity.PasswordHash = string.Empty;
            entity.PasswordSalt = string.Empty;
            await db.SaveChangesAsync();
        }

        foreach (var attempt in new[] { "", " ", "anything", "password" })
        {
            var response = await _factory!.CreateClient().PostAsJsonAsync(
                LoginPath, new { email, password = attempt });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [SkippableFact]
    public async Task ManyCredentialLessWorkersCoexistInOneOrganization()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        // The reason Email became nullable rather than defaulting to an empty string: the
        // unique index on (OrganizationId, Email) would otherwise admit exactly one.
        await CreateWorkerAsync(tenant, "Worker One");
        await CreateWorkerAsync(tenant, "Worker Two");
        await CreateWorkerAsync(tenant, "Worker Three");

        await using var db = _postgres.CreateContext();
        var count = await db.Users.AsNoTracking().CountAsync(
            x => x.OrganizationId == tenant.OrganizationId
                 && x.CredentialType == UserCredentialType.ActivationOnly);

        Assert.Equal(3, count);
    }

    // ─── Leakage ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheActivationResponseCarriesNoSecretMaterial()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Quiet");
        var issued = await IssueAsync(tenant, worker.Id);

        var response = await RawActivateAsync(issued.Code, "handset-" + Guid.NewGuid().ToString("N"));
        var body = await response.Content.ReadAsStringAsync();

        // The submitted code must never come back, and neither must anything derived from it.
        Assert.DoesNotContain(issued.Code, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("codeHash", body, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task AnUnknownCodeAndASpentCodeAreIndistinguishable()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Oracle");
        var issued = await IssueAsync(tenant, worker.Id);
        await ActivateAsync(issued.Code, "handset-" + Guid.NewGuid().ToString("N"));

        var spent = await RawActivateAsync(issued.Code, "handset-" + Guid.NewGuid().ToString("N"));
        var unknown = await RawActivateAsync("ZAZI-ABCD-EFGH-JKMN-PQRS", "handset-" + Guid.NewGuid().ToString("N"));

        // Differing responses would let an attacker learn which codes exist. The correlation
        // id is per-request by design and is the one thing expected to differ, so it is
        // stripped rather than compared — asserting on it would have failed for a reason that
        // has nothing to do with disclosure.
        Assert.Equal(spent.StatusCode, unknown.StatusCode);
        Assert.Equal(
            WithoutCorrelationId(await spent.Content.ReadAsStringAsync()),
            WithoutCorrelationId(await unknown.Content.ReadAsStringAsync()));
    }

    // ─── Revocation ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task RevokingADeviceKillsTheSessionItIssued()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Departing");
        var issued = await IssueAsync(tenant, worker.Id);
        var activated = await ActivateAsync(issued.Code, "handset-" + Guid.NewGuid().ToString("N"));

        var revoke = await ManagerClient(tenant)
            .PostAsync($"/api/v1/devices/{activated.DeviceId}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        // Marking the device is not enough on its own — the handset still holds a refresh
        // token. If this passes, revoking a stolen phone means it stops working at its next
        // contact rather than whenever its token happens to age out.
        var refresh = await _factory!.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/refresh", new { refreshToken = activated.Session.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [SkippableFact]
    public async Task ARevokedDeviceIsMarkedRevokedNotMerelyDeactivated()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Marked");
        var issued = await IssueAsync(tenant, worker.Id);
        var activated = await ActivateAsync(issued.Code, "handset-" + Guid.NewGuid().ToString("N"));

        await ManagerClient(tenant).PostAsync($"/api/v1/devices/{activated.DeviceId}/revoke", null);

        await using var db = _postgres.CreateContext();
        var device = await db.Devices.AsNoTracking().SingleAsync(x => x.Id == activated.DeviceId);

        // The handset reads this through /devices/me to decide it has been cut off, so both
        // the flag and the status have to say so.
        Assert.True(device.IsRevoked);
        Assert.Equal(DeviceStatus.Revoked, device.Status);
    }

    [SkippableFact]
    public async Task OneBusinessCannotRevokeAnothersDevice()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var intruder = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Not Yours");
        var issued = await IssueAsync(tenant, worker.Id);
        var activated = await ActivateAsync(issued.Code, "handset-" + Guid.NewGuid().ToString("N"));

        // A manager in a different business, holding a perfectly valid token of their own.
        var response = await ManagerClient(intruder)
            .PostAsync($"/api/v1/devices/{activated.DeviceId}/revoke", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var db = _postgres.CreateContext();
        var device = await db.Devices.AsNoTracking().SingleAsync(x => x.Id == activated.DeviceId);
        Assert.NotEqual(DeviceStatus.Revoked, device.Status);
    }

    [SkippableFact]
    public async Task ARevokedDeviceCannotBeReActivatedWithANewCode()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Returning");
        var identifier = "handset-" + Guid.NewGuid().ToString("N");
        var activated = await ActivateAsync((await IssueAsync(tenant, worker.Id)).Code, identifier);

        await ManagerClient(tenant).PostAsync($"/api/v1/devices/{activated.DeviceId}/revoke", null);

        // A fresh code on the same handset. Re-admitting it would make revocation a
        // formality, so the identifier stays taken until the owner clears the device record.
        var response = await RawActivateAsync((await IssueAsync(tenant, worker.Id)).Code, identifier);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private Task<TenantSeed> SeedAsync() => TenantSeedFactory.CreateAsync(_postgres);

    private static string WithoutCorrelationId(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body, "\"correlationId\":\"[^\"]*\"", "");

    private HttpClient ManagerClient(TenantSeed tenant) =>
        _factory!.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.BranchManager));

    private async Task<UserDto> CreateWorkerAsync(TenantSeed tenant, string fullName)
    {
        var response = await ManagerClient(tenant).PostAsJsonAsync(WorkersPath, new
        {
            branchId = tenant.BranchId,
            fullName,
            roles = new[] { ZaziRoles.Agent }
        });

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserDto>())!;
    }

    private async Task<EnrollmentCodeIssuedDto> IssueAsync(TenantSeed tenant, Guid? intendedUserId)
    {
        var response = await ManagerClient(tenant).PostAsJsonAsync(CodesPath, new
        {
            branchId = tenant.BranchId,
            intendedUserId
        });

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EnrollmentCodeIssuedDto>())!;
    }

    private Task<HttpResponseMessage> RawActivateAsync(string code, string deviceIdentifier) =>
        _factory!.CreateClient().PostAsJsonAsync(ActivatePath, new
        {
            code,
            deviceIdentifier,
            name = "Test handset",
            platform = "Android",
            network = "MTN",
            appVersion = "2.0.0",
            osVersion = "37"
        });

    private async Task<DeviceActivationResult> ActivateAsync(string code, string deviceIdentifier)
    {
        var response = await RawActivateAsync(code, deviceIdentifier);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeviceActivationResult>())!;
    }

    private async Task MutateCodeAsync(Guid codeId, Action<DeviceEnrollmentCode> mutate)
    {
        await using var db = _postgres.CreateContext();
        var code = await db.DeviceEnrollmentCodes.SingleAsync(x => x.Id == codeId);
        mutate(code);
        await db.SaveChangesAsync();
    }
}
