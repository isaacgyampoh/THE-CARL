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
    private readonly Bunit.TestDoubles.TestAuthorizationContext _authorization;

    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly Guid BranchId = Guid.NewGuid();
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid DeviceId = Guid.NewGuid();
    private static readonly Guid WorkerId = Guid.NewGuid();
    private static readonly Guid OtherBranchId = Guid.NewGuid();
    private static readonly Guid OtherDeviceId = Guid.NewGuid();
    private static readonly Guid OtherWorkerId = Guid.NewGuid();

    public TeamPageTests()
    {
        Services.AddSingleton<IDeviceService>(_devices);
        Services.AddSingleton<IAuthService>(_auth);
        Services.AddSingleton<IDeviceEnrollmentService>(_enrollment);
        Services.AddSingleton<IOrganizationService>(new StubOrganizations());
        Services.AddSingleton<ICurrentUserContext>(new StubCurrentUser(organizationWide: true));
        _authorization = this.AddTestAuthorization();
        _authorization.SetAuthorized("Kwame Mensah");
        // The existing tests predate policy-gated sections and assume the owner can do
        // everything the page offers, which is what an owner's roles actually grant.
        _authorization.SetPolicies(ZaziPolicies.BranchRead, ZaziPolicies.BranchManage,
            ZaziPolicies.StaffManage, ZaziPolicies.DeviceManage);
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

        // Null here because this caller is an owner. For a branch manager it would be their
        // branch, which is what stops the portal revoking a handset it does not even list.
        Assert.Null(_devices.LastRequiredBranchId);
    }

    [Fact]
    public void ABranchManagerRevokingPassesTheirBranchAsAConstraint()
    {
        // Hiding another branch's device from the listing is not an authorisation control —
        // the service acts on whatever id it is handed. The page has to say which branch this
        // caller may act in, so the service can refuse rather than trust the id.
        Services.AddSingleton<ICurrentUserContext>(
            new StubCurrentUser(organizationWide: false, branchId: BranchId));

        var page = RenderComponent<Team>();
        page.FindAll("button").First(b => b.TextContent.Contains("Revoke")).Click();
        page.FindAll("button").First(b => b.TextContent.Contains("Yes, revoke")).Click();

        Assert.Equal(BranchId, _devices.LastRequiredBranchId);
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

    // ─── Branch scoping ──────────────────────────────────────────────────────

    [Fact]
    public void AnOwnerSeesEveryBranch()
    {
        var page = RenderComponent<Team>();

        Assert.Contains("Ama Mensah", page.Markup);
        Assert.Contains("Kojo Antwi", page.Markup);
    }

    [Fact]
    public void ABranchManagerSeesOnlyTheirOwnBranchsWorkersAndDevices()
    {
        // The defect this catches: the branch filter lived in the API controllers, and this
        // page calls the services directly, so it skipped the filter entirely and showed a
        // branch manager the whole business.
        Services.AddSingleton<ICurrentUserContext>(
            new StubCurrentUser(organizationWide: false, branchId: BranchId));

        var page = RenderComponent<Team>();

        Assert.Contains("Ama Mensah", page.Markup);
        Assert.DoesNotContain("Kojo Antwi", page.Markup);
        Assert.Contains("Ama's phone", page.Markup);
        Assert.DoesNotContain("Kumasi phone", page.Markup);
    }

    [Fact]
    public void ABranchManagerIsOnlyOfferedBranchesTheyCanWriteTo()
    {
        Services.AddSingleton<ICurrentUserContext>(
            new StubCurrentUser(organizationWide: false, branchId: BranchId));

        var page = RenderComponent<Team>();

        // Offering the others would let them fill in a form the tenant guard then rejects.
        var options = page.FindAll("option").Select(o => o.TextContent).ToList();
        Assert.Contains("Accra Central", options);
        Assert.DoesNotContain("Kumasi", options);
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
            // Deliberately unfiltered, exactly as the real service is: the branch rule is the
            // caller's to apply, and the whole point of these tests is whether the page
            // applies it.
            Task.FromResult<IReadOnlyList<DeviceDto>>(new[]
            {
                new DeviceDto(
                    DeviceId, OrganizationId, BranchId, "Ama's phone", "installation-1",
                    "Android", "MTN", DeviceRole.TransactionDevice, DeviceStatus.Active,
                    "2.0.0", "37", DateTimeOffset.UtcNow),
                new DeviceDto(
                    OtherDeviceId, OrganizationId, OtherBranchId, "Kumasi phone", "installation-2",
                    "Android", "MTN", DeviceRole.TransactionDevice, DeviceStatus.Active,
                    "2.0.0", "37", DateTimeOffset.UtcNow)
            });

        public Guid? LastRequiredBranchId { get; private set; }

        public Task RevokeDeviceAsync(
            Guid deviceId, Guid organizationId, Guid actorUserId,
            Guid? requiredBranchId = null, CancellationToken ct = default)
        {
            Revoked.Add(deviceId);
            LastOrganizationId = organizationId;
            LastActorId = actorUserId;
            LastRequiredBranchId = requiredBranchId;
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
            Task.FromResult<IReadOnlyList<UserDto>>(new[] { Worker(), OtherBranchWorker() });

        private static UserDto Worker() => new(
            WorkerId, OrganizationId, BranchId, "Ama Mensah", null, null,
            true, false, false, DateTimeOffset.UtcNow,
            UserCredentialType.ActivationOnly, new[] { "AGENT" });

        private static UserDto OtherBranchWorker() => new(
            OtherWorkerId, OrganizationId, OtherBranchId, "Kojo Antwi", null, null,
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

        public Task<KeypadPhoneLink> LinkKeypadPhoneAsync(string code, string phoneNumber, CancellationToken ct = default) =>
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
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new BranchDto(OtherBranchId, OrganizationId, "Kumasi", null,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            });
    }

    private sealed class StubCurrentUser : ICurrentUserContext
    {
        private readonly bool _organizationWide;
        private readonly Guid? _branchId;

        public StubCurrentUser(bool organizationWide = true, Guid? branchId = null)
        {
            _organizationWide = organizationWide;
            _branchId = branchId;
        }

        public bool IsAuthenticated => true;
        public Guid UserId => OwnerId;
        public Guid OrganizationId => TeamPageTests.OrganizationId;
        public Guid? BranchId => _branchId;
        public IReadOnlyCollection<string> Roles => _organizationWide ? new[] { "OWNER" } : new[] { "BRANCH_MANAGER" };
        public bool HasOrganizationWideScope => _organizationWide;
        public string? SecurityStamp => "stamp";
        public string CorrelationId => "test-correlation";

        public bool IsInRole(string role) => Roles.Contains(role, StringComparer.Ordinal);

        // An owner sees their whole business, so these are permissive here. The real
        // enforcement is server-side and is covered by the tenant-isolation suites; this stub
        // exists only so the page can render.
        public bool CanAccessBranch(Guid branchId) => _organizationWide || branchId == _branchId;

        public void EnsureBranchAccess(Guid branchId) { }

        public void EnsureOrganizationMatches(Guid organizationId)
        {
            if (organizationId != OrganizationId)
            {
                throw new TenantAccessDeniedException("Wrong tenant.");
            }
        }
    }

    // ─── Branches ────────────────────────────────────────────────────────────

    [Fact]
    public void SomeoneWhoMayManageBranchesIsOfferedTheForm()
    {
        _authorization.SetAuthorized("Kwame Mensah");
        _authorization.SetPolicies(ZaziPolicies.BranchRead, ZaziPolicies.BranchManage);

        var page = RenderComponent<portal::Zazi.Web.Components.Pages.Team>();

        Assert.Contains("Add branch", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatingBranchesIsNotSomethingABranchManagerMayDo()
    {
        // The page requires StaffManage, which a branch manager holds. The branch form is gated
        // on BranchManage, which they must not — otherwise reaching the page would quietly
        // grant the wider permission.
        //
        // Asserted against the policy table rather than the rendered markup, because bUnit's
        // fake authorization does not evaluate AuthorizeView policies at all: it renders
        // authorized content whatever policy is asked for. A markup assertion here would pass
        // no matter what the page did, which is worse than no test.
        var mayManageBranches = ZaziPolicies.RolesByPolicy[ZaziPolicies.BranchManage];
        var mayManageStaff = ZaziPolicies.RolesByPolicy[ZaziPolicies.StaffManage];

        Assert.Contains(ZaziRoles.BranchManager, mayManageStaff);
        Assert.DoesNotContain(ZaziRoles.BranchManager, mayManageBranches);

        // And an owner, who the page is really for, may do both.
        Assert.Contains(ZaziRoles.Owner, mayManageBranches);
    }

    [Fact]
    public void CreatingABranchPassesTheOwnersOrganizationNotTheFormsWord()
    {
        var organizations = new RecordingOrganizations();
        Services.AddSingleton<IOrganizationService>(organizations);
        _authorization.SetAuthorized("Kwame Mensah");
        _authorization.SetPolicies(ZaziPolicies.BranchRead, ZaziPolicies.BranchManage);

        var page = RenderComponent<portal::Zazi.Web.Components.Pages.Team>();
        page.Find("#branch-name").Change("Kumasi Central");
        page.Find("#branch-location").Change("Adum");
        page.Find("#add-branch-submit").Click();

        var created = Assert.Single(organizations.Created);
        Assert.Equal("Kumasi Central", created.Name);
        Assert.Equal("Adum", created.Location);

        // The organization comes from the signed-in identity, never from the posted form —
        // otherwise creating a branch inside someone else's business would be a field edit.
        Assert.Equal(OrganizationId, created.OrganizationId);
    }

    [Fact]
    public void AnEmptyLocationIsStoredAsNothingRatherThanBlank()
    {
        var organizations = new RecordingOrganizations();
        Services.AddSingleton<IOrganizationService>(organizations);
        _authorization.SetAuthorized("Kwame Mensah");
        _authorization.SetPolicies(ZaziPolicies.BranchRead, ZaziPolicies.BranchManage);

        var page = RenderComponent<portal::Zazi.Web.Components.Pages.Team>();
        page.Find("#branch-name").Change("Tema");
        page.Find("#add-branch-submit").Click();

        Assert.Null(Assert.Single(organizations.Created).Location);
    }

    private sealed class RecordingOrganizations : IOrganizationService
    {
        public List<CreateBranchRequest> Created { get; } = new();

        public Task<BranchDto?> CreateBranchAsync(CreateBranchRequest request, CancellationToken cancellationToken = default)
        {
            Created.Add(request);
            return Task.FromResult<BranchDto?>(new BranchDto(Guid.NewGuid(), request.OrganizationId, request.Name, request.Location, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<BranchDto>> GetBranchesAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BranchDto>>(new[] { new BranchDto(BranchId, organizationId, "Main branch", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) });

        public Task<OrganizationDto?> GetOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<OrganizationDto?>(null);

        public Task<OrganizationDto> CreateOrganizationAsync(CreateOrganizationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<OrganizationDto>> GetOrganizationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OrganizationDto>>(Array.Empty<OrganizationDto>());
    }

}
