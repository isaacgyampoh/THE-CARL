namespace Zazi.Application.Security;

/// <summary>
/// Server-side ownership checks for tenant-scoped entities.
/// <para>
/// Role checks alone are not sufficient for isolation: a BRANCH_MANAGER in organization A
/// holds the same role as a BRANCH_MANAGER in organization B. Every request that names an
/// entity by id must also prove that entity belongs to the caller's organization, and that
/// the caller's branch scope covers it.
/// </para>
/// </summary>
public interface ITenantGuard
{
    /// <summary>
    /// Confirms the branch exists inside the caller's organization and is within the
    /// caller's branch scope. Throws <see cref="TenantAccessDeniedException"/> otherwise.
    /// </summary>
    Task EnsureBranchInTenantAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms the session belongs to the caller's organization and branch scope,
    /// and returns its branch id.
    /// </summary>
    Task<Guid> EnsureSessionInTenantAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Confirms the device belongs to the caller's organization and branch scope.</summary>
    Task EnsureDeviceInTenantAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the branch the caller is acting on: the supplied branch when they are
    /// permitted to name one, otherwise their own assigned branch. Throws when neither
    /// yields a branch the caller may use.
    /// </summary>
    Task<Guid> ResolveWritableBranchAsync(Guid? requestedBranchId, CancellationToken cancellationToken = default);
}
