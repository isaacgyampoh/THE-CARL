extern alias portal;

using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Domain;
using Team = portal::Zazi.Web.Components.Pages.Team;

namespace Zazi.IntegrationTests;

/// <summary>
/// The owner's Team page, driven through its actual buttons.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the page's interactive actions run over SignalR and cannot be reached
/// with an HTTP request, so everything below was previously verifiable only by a person
/// clicking it. Revoking a device cuts a working phone off mid-shift and cannot be undone
/// from this page — it is not something to leave untested because the transport is awkward.
/// </para>
/// <para>
/// The services are stubbed rather than mocked through a framework: what matters is whether
/// the page calls them, and with what, which a recording stub states more plainly.
/// </para>
/// </remarks>
public class TeamPageTests : TestContext
{
    private readonly RecordingDevices _devices = new();
    private readonly RecordingAuth _auth = new();
    private readonly RecordingEnrollment _enrollment = new();

    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly Guid BranchId = Guid.NewGuid();
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid DeviceId = Guid.NewGuid();
    private static readonly Guid WorkerId = Guid.NewGuid();

    public TeamPageTests()
    {
        Services.AddSingleton<IDeviceService>(_devices);
        Services.AddSingleton<IAuthService>(_auth);
        Services.AddSingleton<IDeviceEnrollmentService>(_enrollment);
        Services.AddSingleton<IOrganizationService>(new StubOrganizations());
        Services.AddSingleton<ICurrentUserContext>(new StubCurrentUser());
        this.AddTestAuthorization().SetAuthorized("Kwame Mensah");
    }

    // ─── Revocation takes two deliberate steps ───────────────────────────────

    [Fact]
    public void RevokingAsksForConfirmationRatherThanActingOnTheFirstClick()
    {
        var page = RenderComponent<Team>();

        page.FindAll("button").First(b => b.TextContent.Contains("Revoke")).Click();

        // The whole point: one click arms it, it does not fire it.
        Assert.Empty(_devices.Revoked);
        Assert.Contains("Cut this device off?", page.Markup);
    }

    [Fact]
    public void ConfirmingRevokesTheDevice()
    {
        var page = RenderComponent<Team>();

        page.FindAll("button").First(b => b.TextContent.Contains("Revoke")).Click();
        page.FindAll("button").First(b => b.TextContent.Contains("Yes, revoke")).Click();

        Assert.Equal(new[] { DeviceId }, _devices.Revoked);
    }

    [Fact]
    public void CancellingLeavesTheDeviceAlone()
    {
        var page = RenderComponent<Team>();

        page.FindAll("button").First(b => b.TextContent.Contains("Revoke")).Click();
        page.FindAll("button").First(b => b.TextContent.Contains("Cancel")).Click();

        Assert.Empty(_devices.Revoked);
        Assert.DoesNotContain("Cut this device off?", page.Markup);
    }

    [Fact]
    public void RevocationIsScopedToTheOwnersOwnOrganization()
    {
        var page = RenderComponent<Team>();

        page.FindAll("button").First(b => b.TextContent.Contains("Revoke")).Click();
        page.FindAll("button").First(b => b.TextContent.Contains("Yes, revoke")).Click();

        // The page must never let the caller's tenant be anything but their own.
        Assert.Equal(OrganizationId, _devices.LastOrganizationId);
        Assert.Equal(OwnerId, _devices.LastActorId);
    }

    // ─── Activation codes ────────────────────────────────────────────────────

    [Fact]
    public void GeneratingACodeShowsThePlaintextAndSaysItIsShownOnce()
    {
        var page = RenderComponent<Team>();

        page.FindAll("button").First(b => b.TextContent.Contains("Generate activation code")).Click();

        // The server keeps only a hash, so if the owner navigates away without copying this
        // it is gone. The page has to say so, not merely display it.
        Assert.Contains("ZAZI-TEST-CODE", page.Markup);
        Assert.Contains("shown once", page.Markup);
    }

    [Fact]
    public void ACodeIsBoundToTheWorkerItWasGeneratedFor()
    {
        var page = RenderComponent<Team>();

        page.FindAll("button").First(b => b.TextContent.Contains("Generate activation code")).Click();

        // Without an intended user the code carries no identity and the anonymous activation
        // endpoint refuses it, so this is what makes the code usable at all.
        Assert.Equal(WorkerId, _enrollment.LastIntendedUserId);
        Assert.Equal(BranchId, _enrollment.LastBranchId);
    }

    // ─── What the page shows ─────────────────────────────────────────────────

    [Fact]
    public void AWorkerIsShownAsUsingACodeRatherThanAsHavingNoEmail()
    {
        var page = RenderComponent<Team>();

        // Stated, not inferred from a blank cell.
        Assert.Contains("Activation code", page.Markup);
        Assert.Contains("Ama Mensah", page.Markup);
    }

    [Fact]
    public void TheDeviceListSaysWhenRevocationTakesEffect()
    {
        var page = RenderComponent<Team>();

        // An owner acting on a stolen phone needs to know this before they rely on it.
        Assert.Contains("next time that phone reaches Zazi", page.Markup);
    }

    // ─── Stubs ───────────────────────────────────────────────────────────────

    private sealed class RecordingDevices : IDeviceService
    {
        public List<Guid> Revoked { get; } = new();
        public Guid LastOrganizationId { get; private set; }
        public Guid LastActorId { get; private set; }

        public Task<DeviceDto> RegisterDeviceAsync(CreateDeviceRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(Guid organizationId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeviceDto>>(new[]
            {
                new DeviceDto(
                    DeviceId, OrganizationId, BranchId, "Ama's phone", "installation-1",
                    "Android", "MTN", DeviceRole.TransactionDevice, DeviceStatus.Active,
                    "2.0.0", "37", DateTimeOffset.UtcNow)
            });

        public Task RevokeDeviceAsync(Guid deviceId, Guid organizationId, Guid actorUserId, CancellationToken ct = default)
        {
            Revoked.Add(deviceId);
            LastOrganizationId = organizationId;
            LastActorId = actorUserId;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAuth : IAuthService
    {
        public Task<UserDto> RegisterUserAsync(RegisterUserRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AuthTokenResult> LoginAsync(LoginRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AuthTokenResult> RefreshTokenAsync(RefreshTokenRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AuthTokenResult> IssueActivationSessionAsync(Guid userId, Guid deviceId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<UserDto> CreateWorkerAsync(CreateWorkerRequest request, CancellationToken ct = default) =>
            Task.FromResult(Worker());

        public Task<IReadOnlyList<UserDto>> GetUsersAsync(Guid organizationId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserDto>>(new[] { Worker() });

        private static UserDto Worker() => new(
            WorkerId, OrganizationId, BranchId, "Ama Mensah", null, null,
            true, false, false, DateTimeOffset.UtcNow,
            UserCredentialType.ActivationOnly, new[] { "AGENT" });
    }

    private sealed class RecordingEnrollment : IDeviceEnrollmentService
    {
        public Guid? LastIntendedUserId { get; private set; }
        public Guid LastBranchId { get; private set; }

        public Task<EnrollmentCodeIssuedDto> IssueCodeAsync(
            IssueEnrollmentCodeRequest request, Guid organizationId, Guid branchId, Guid issuedByUserId,
            CancellationToken ct = default)
        {
            LastIntendedUserId = request.IntendedUserId;
            LastBranchId = branchId;
            return Task.FromResult(new EnrollmentCodeIssuedDto(
                Guid.NewGuid(), "ZAZI-TEST-CODE", "ZAZI-TEST", organizationId, branchId,
                DeviceRole.TransactionDevice, DateTimeOffset.UtcNow.AddHours(24), null));
        }

        public Task<IReadOnlyList<EnrollmentCodeDto>> GetCodesAsync(Guid organizationId, Guid? branchId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EnrollmentCodeDto>>(Array.Empty<EnrollmentCodeDto>());

        public Task RevokeCodeAsync(Guid codeId, Guid organizationId, Guid revokedByUserId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<DeviceEnrolledDto> RedeemAsync(RedeemEnrollmentCodeRequest request, Guid organizationId, Guid redeemingUserId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DeviceActivationResult> ActivateAsync(ActivateDeviceRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DeviceSelfDto?> GetDeviceSelfAsync(Guid organizationId, string deviceIdentifier, CancellationToken ct = default) =>
            Task.FromResult<DeviceSelfDto?>(null);
    }

    private sealed class StubOrganizations : IOrganizationService
    {
        public Task<IReadOnlyList<OrganizationDto>> GetOrganizationsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OrganizationDto> CreateOrganizationAsync(CreateOrganizationRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<BranchDto?> CreateBranchAsync(CreateBranchRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BranchDto>> GetBranchesAsync(Guid organizationId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BranchDto>>(new[]
            {
                new BranchDto(BranchId, OrganizationId, "Accra Central", null,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            });
    }

    private sealed class StubCurrentUser : ICurrentUserContext
    {
        public bool IsAuthenticated => true;
        public Guid UserId => OwnerId;
        public Guid OrganizationId => TeamPageTests.OrganizationId;
        public Guid? BranchId => null;
        public IReadOnlyCollection<string> Roles => new[] { "OWNER" };
        public bool HasOrganizationWideScope => true;
        public string? SecurityStamp => "stamp";
        public string CorrelationId => "test-correlation";

        public bool IsInRole(string role) => Roles.Contains(role, StringComparer.Ordinal);

        // An owner sees their whole business, so these are permissive here. The real
        // enforcement is server-side and is covered by the tenant-isolation suites; this stub
        // exists only so the page can render.
        public bool CanAccessBranch(Guid branchId) => true;

        public void EnsureBranchAccess(Guid branchId) { }

        public void EnsureOrganizationMatches(Guid organizationId)
        {
            if (organizationId != OrganizationId)
            {
                throw new TenantAccessDeniedException("Wrong tenant.");
            }
        }
    }
}
