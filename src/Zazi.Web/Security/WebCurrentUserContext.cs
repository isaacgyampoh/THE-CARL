using System.Security.Claims;
using Zazi.Application.Security;

namespace Zazi.Web.Security;

/// <summary>
/// Tenant identity for the browser session, read from the authentication cookie.
/// </summary>
/// <remarks>
/// <para>
/// A thin transport adapter only. Every interpretation of the claims — which organization,
/// which branch, which roles, and whether a branch may be touched — comes from
/// <see cref="ClaimsTenantIdentity"/>, the same code the API's bearer-token path uses. The
/// web application therefore cannot drift into a more permissive view of a tenant boundary
/// than the API has.
/// </para>
/// <para>
/// The cookie is issued only after <c>IAuthService</c> has verified the password, so this
/// introduces no second credential check and no second user store.
/// </para>
/// </remarks>
public sealed class WebCurrentUserContext : ICurrentUserContext
{
    private readonly IHttpContextAccessor _accessor;

    public WebCurrentUserContext(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public bool IsAuthenticated => ClaimsTenantIdentity.IsAuthenticated(Principal);

    public Guid UserId => ClaimsTenantIdentity.UserId(Principal);

    public Guid OrganizationId => ClaimsTenantIdentity.OrganizationId(Principal);

    public Guid? BranchId => ClaimsTenantIdentity.BranchId(Principal);

    public IReadOnlyCollection<string> Roles => ClaimsTenantIdentity.Roles(Principal);

    public string? SecurityStamp => ClaimsTenantIdentity.SecurityStamp(Principal);

    public string CorrelationId => _accessor.HttpContext?.TraceIdentifier ?? string.Empty;

    public bool IsInRole(string canonicalRole) => Roles.Contains(canonicalRole, StringComparer.Ordinal);

    public bool HasOrganizationWideScope => ClaimsTenantIdentity.HasOrganizationWideScope(Principal);

    public bool CanAccessBranch(Guid branchId) =>
        ClaimsTenantIdentity.CanAccessBranch(Principal, branchId);

    public void EnsureBranchAccess(Guid branchId)
    {
        if (!CanAccessBranch(branchId))
        {
            throw new TenantAccessDeniedException(
                "The authenticated caller is not permitted to act on the requested branch.");
        }
    }

    public void EnsureOrganizationMatches(Guid organizationId)
    {
        if (organizationId == Guid.Empty || OrganizationId != organizationId)
        {
            throw new TenantAccessDeniedException(OrganizationId, organizationId);
        }
    }
}
