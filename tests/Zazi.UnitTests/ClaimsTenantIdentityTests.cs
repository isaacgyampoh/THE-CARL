using System.Security.Claims;
using Zazi.Application.Security;

namespace Zazi.UnitTests;

/// <summary>
/// The single reader of tenant identity from claims.
/// </summary>
/// <remarks>
/// Both transports depend on this: the API authenticates with a bearer token and the web
/// application with a cookie. A mistake here is not a display bug — it is the one class of
/// mistake that shows one tenant another tenant's money.
/// </remarks>
public class ClaimsTenantIdentityTests
{
    private static readonly Guid Organization = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Branch = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherBranch = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid User = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void ReadsIdentityFromAValidatedPrincipal()
    {
        var principal = Principal(ZaziRoles.BranchManager, branch: Branch);

        Assert.True(ClaimsTenantIdentity.IsAuthenticated(principal));
        Assert.Equal(User, ClaimsTenantIdentity.UserId(principal));
        Assert.Equal(Organization, ClaimsTenantIdentity.OrganizationId(principal));
        Assert.Equal(Branch, ClaimsTenantIdentity.BranchId(principal));
    }

    [Fact]
    public void AnUnauthenticatedPrincipalHasNoTenant()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False(ClaimsTenantIdentity.IsAuthenticated(anonymous));

        // Throwing beats returning Guid.Empty: an empty tenant id silently matches nothing,
        // or worse, matches a row someone created with a default value.
        Assert.Throws<TenantAccessDeniedException>(() => ClaimsTenantIdentity.OrganizationId(anonymous));
        Assert.Throws<TenantAccessDeniedException>(() => ClaimsTenantIdentity.UserId(anonymous));
    }

    [Fact]
    public void AMissingOrganizationClaimIsRefusedRatherThanDefaulted()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, User.ToString())
        }, "test");

        Assert.Throws<TenantAccessDeniedException>(
            () => ClaimsTenantIdentity.OrganizationId(new ClaimsPrincipal(identity)));
    }

    [Fact]
    public void AnEmptyGuidClaimIsTreatedAsAbsent()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, User.ToString()),
            new Claim(ZaziClaimTypes.OrganizationId, Organization.ToString()),
            new Claim(ZaziClaimTypes.BranchId, Guid.Empty.ToString())
        }, "test");

        Assert.Null(ClaimsTenantIdentity.BranchId(new ClaimsPrincipal(identity)));
    }

    [Fact]
    public void BranchStaffReachOnlyTheirOwnBranch()
    {
        var principal = Principal(ZaziRoles.Agent, branch: Branch);

        Assert.True(ClaimsTenantIdentity.CanAccessBranch(principal, Branch));
        Assert.False(ClaimsTenantIdentity.CanAccessBranch(principal, OtherBranch));
        Assert.False(ClaimsTenantIdentity.HasOrganizationWideScope(principal));
    }

    [Fact]
    public void OrganizationWideStaffReachEveryBranch()
    {
        var principal = Principal(ZaziRoles.Owner, branch: null);

        Assert.True(ClaimsTenantIdentity.HasOrganizationWideScope(principal));
        Assert.True(ClaimsTenantIdentity.CanAccessBranch(principal, Branch));
        Assert.True(ClaimsTenantIdentity.CanAccessBranch(principal, OtherBranch));
    }

    [Fact]
    public void AnEmptyBranchIdIsNeverAccessible()
    {
        // Guards the shape of an unset filter reaching an access check.
        Assert.False(ClaimsTenantIdentity.CanAccessBranch(Principal(ZaziRoles.Owner, null), Guid.Empty));
    }

    [Fact]
    public void BothRoleClaimSpellingsAreUnderstood()
    {
        // The bearer path may or may not map inbound claim names depending on configuration,
        // and the cookie path writes the short form. Reading only one would silently strip a
        // caller's roles and, for an owner, downgrade them to branch scope.
        var shortForm = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ZaziClaimTypes.Role, ZaziRoles.Owner)
        }, "test"));

        var uriForm = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Role, ZaziRoles.Owner)
        }, "test"));

        Assert.Contains(ZaziRoles.Owner, ClaimsTenantIdentity.Roles(shortForm));
        Assert.Contains(ZaziRoles.Owner, ClaimsTenantIdentity.Roles(uriForm));
    }

    [Fact]
    public void AnUnknownRoleIsDiscardedRatherThanTrusted()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ZaziClaimTypes.Role, "SUPER_ADMIN_TOTALLY_REAL")
        }, "test"));

        Assert.Empty(ClaimsTenantIdentity.Roles(principal));
        Assert.False(ClaimsTenantIdentity.HasOrganizationWideScope(principal));
    }

    private static ClaimsPrincipal Principal(string role, Guid? branch)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, User.ToString()),
            new(ZaziClaimTypes.OrganizationId, Organization.ToString()),
            new(ZaziClaimTypes.Role, role)
        };

        if (branch is { } value)
        {
            claims.Add(new Claim(ZaziClaimTypes.BranchId, value.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
