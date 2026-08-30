using System.Security.Claims;
using Zazi.Application.Security;

namespace Zazi.Api.Security;

/// <summary>
/// Resolves the caller's tenant identity from validated access-token claims on the
/// current <see cref="HttpContext"/>. Every value here originates from a signature-checked
/// token, never from route, query, header or body input.
/// </summary>
public sealed class HttpCurrentUserContext : ICurrentUserContext
{
    public const string CorrelationHeader = "X-Correlation-Id";

    private readonly IHttpContextAccessor _accessor;

    public HttpCurrentUserContext(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid UserId => RequireGuidClaim(ClaimTypes.NameIdentifier, "sub");

    public Guid OrganizationId => RequireGuidClaim(ZaziClaimTypes.OrganizationId);

    public Guid? BranchId
    {
        get
        {
            var raw = Principal?.FindFirst(ZaziClaimTypes.BranchId)?.Value;
            return Guid.TryParse(raw, out var value) && value != Guid.Empty ? value : null;
        }
    }

    public IReadOnlyCollection<string> Roles
    {
        get
        {
            var principal = Principal;
            if (principal is null)
            {
                return Array.Empty<string>();
            }

            // Accept both the short "role" claim and the full ClaimTypes.Role URI, because
            // the JWT handler may or may not map inbound claim names depending on config.
            return principal.Claims
                .Where(c => c.Type is ZaziClaimTypes.Role or ClaimTypes.Role)
                .Select(c => ZaziRoles.Normalize(c.Value))
                .Where(r => r is not null)
                .Select(r => r!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
    }

    public string? SecurityStamp => Principal?.FindFirst(ZaziClaimTypes.SecurityStamp)?.Value;

    public string CorrelationId
    {
        get
        {
            var context = _accessor.HttpContext;
            if (context is null)
            {
                return string.Empty;
            }

            return context.Response.Headers.TryGetValue(CorrelationHeader, out var value) && value.Count > 0
                ? value[0] ?? context.TraceIdentifier
                : context.TraceIdentifier;
        }
    }

    public bool IsInRole(string canonicalRole) => Roles.Contains(canonicalRole, StringComparer.Ordinal);

    public bool HasOrganizationWideScope => Roles.Any(ZaziRoles.IsOrganizationWide);

    public bool CanAccessBranch(Guid branchId)
    {
        if (!IsAuthenticated || branchId == Guid.Empty)
        {
            return false;
        }

        return HasOrganizationWideScope || (BranchId.HasValue && BranchId.Value == branchId);
    }

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

    private Guid RequireGuidClaim(params string[] claimTypes)
    {
        var principal = Principal
            ?? throw new NotAuthenticatedException();

        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new NotAuthenticatedException();
        }

        foreach (var claimType in claimTypes)
        {
            var raw = principal.FindFirst(claimType)?.Value;
            if (Guid.TryParse(raw, out var value) && value != Guid.Empty)
            {
                return value;
            }
        }

        throw new NotAuthenticatedException(
            $"The access token is missing a required claim ({string.Join(" or ", claimTypes)}).");
    }
}
