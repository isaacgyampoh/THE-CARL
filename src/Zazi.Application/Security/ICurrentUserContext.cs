namespace Zazi.Application.Security;

/// <summary>
/// The authenticated caller's tenant identity, resolved server-side from the access token.
/// <para>
/// This is the only sanctioned source of <c>OrganizationId</c> for request handling.
/// Controllers must never accept an organization identifier from the client: doing so
/// makes tenant isolation a client-side concern, which it can never be.
/// </para>
/// </summary>
public interface ICurrentUserContext
{
    bool IsAuthenticated { get; }

    /// <summary>The authenticated user's id. Throws if unauthenticated.</summary>
    Guid UserId { get; }

    /// <summary>The tenant the caller belongs to. Throws if unauthenticated.</summary>
    Guid OrganizationId { get; }

    /// <summary>The caller's home branch, or <c>null</c> for organization-wide staff.</summary>
    Guid? BranchId { get; }

    /// <summary>Canonical roles carried by the token.</summary>
    IReadOnlyCollection<string> Roles { get; }

    /// <summary>Security stamp captured at token issue, used to invalidate sessions.</summary>
    string? SecurityStamp { get; }

    /// <summary>Correlation id for the current request, for audit and log stitching.</summary>
    string CorrelationId { get; }

    bool IsInRole(string canonicalRole);

    /// <summary>True when the caller's roles grant visibility across every branch.</summary>
    bool HasOrganizationWideScope { get; }

    /// <summary>
    /// True when the caller may act on <paramref name="branchId"/>: either they have
    /// organization-wide scope, or it is their assigned branch.
    /// </summary>
    bool CanAccessBranch(Guid branchId);

    /// <summary>Throws <see cref="TenantAccessDeniedException"/> when access is not permitted.</summary>
    void EnsureBranchAccess(Guid branchId);

    /// <summary>
    /// Guards against a client supplying an organization id that is not their own.
    /// Throws <see cref="TenantAccessDeniedException"/> on mismatch.
    /// </summary>
    void EnsureOrganizationMatches(Guid organizationId);
}
