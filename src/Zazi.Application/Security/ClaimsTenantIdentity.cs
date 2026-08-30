using System.Security.Claims;

namespace Zazi.Application.Security;

/// <summary>
/// Reads tenant identity from a validated <see cref="ClaimsPrincipal"/>.
/// </summary>
/// <remarks>
/// <para>
/// The API authenticates with a bearer token and the web application with a cookie, but both
/// arrive as the same claims. This is the single place that knows how to interpret them, so a
/// second transport cannot quietly disagree about which organization a caller belongs to —
/// which is the one mistake that would cross a tenant boundary.
/// </para>
/// <para>
/// Every value here originates from a signature- or cookie-validated principal. Nothing is
/// read from route, query, header or body input.
/// </para>
/// </remarks>
public static class ClaimsTenantIdentity
{
    public static bool IsAuthenticated(ClaimsPrincipal? principal) =>
        principal?.Identity?.IsAuthenticated == true;

    public static Guid UserId(ClaimsPrincipal? principal) =>
        RequireGuid(principal, ClaimTypes.NameIdentifier, "sub");

    public static Guid OrganizationId(ClaimsPrincipal? principal) =>
        RequireGuid(principal, ZaziClaimTypes.OrganizationId);

    public static Guid? BranchId(ClaimsPrincipal? principal)
    {
        var raw = principal?.FindFirst(ZaziClaimTypes.BranchId)?.Value;
        return Guid.TryParse(raw, out var value) && value != Guid.Empty ? value : null;
    }

    public static string? SecurityStamp(ClaimsPrincipal? principal) =>
        principal?.FindFirst(ZaziClaimTypes.SecurityStamp)?.Value;

    public static IReadOnlyCollection<string> Roles(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return Array.Empty<string>();
        }

        // Accept both the short "role" claim and the full ClaimTypes.Role URI, because a
        // handler may or may not map inbound claim names depending on configuration.
        return principal.Claims
            .Where(c => c.Type is ZaziClaimTypes.Role or ClaimTypes.Role)
            .Select(c => ZaziRoles.Normalize(c.Value))
            .Where(r => r is not null)
            .Select(r => r!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static bool HasOrganizationWideScope(ClaimsPrincipal? principal) =>
        Roles(principal).Any(ZaziRoles.IsOrganizationWide);

    public static bool CanAccessBranch(ClaimsPrincipal? principal, Guid branchId)
    {
        if (!IsAuthenticated(principal) || branchId == Guid.Empty)
        {
            return false;
        }

        var branch = BranchId(principal);
        return HasOrganizationWideScope(principal) || (branch.HasValue && branch.Value == branchId);
    }

    private static Guid RequireGuid(ClaimsPrincipal? principal, params string[] claimTypes)
    {
        foreach (var type in claimTypes)
        {
            var raw = principal?.FindFirst(type)?.Value;
            if (Guid.TryParse(raw, out var value) && value != Guid.Empty)
            {
                return value;
            }
        }

        throw new TenantAccessDeniedException(
            "The request is not authenticated, or its identity claims are incomplete.");
    }
}
