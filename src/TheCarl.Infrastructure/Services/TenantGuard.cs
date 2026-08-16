using Microsoft.EntityFrameworkCore;
using TheCarl.Application.Security;

namespace TheCarl.Infrastructure.Services;

/// <summary>
/// Enforces that every entity named in a request belongs to the authenticated caller's
/// organization, and lies within their branch scope.
/// </summary>
/// <remarks>
/// Every lookup filters on the caller's organization id from the access token. A
/// "not found in my tenant" and a "does not exist" are deliberately indistinguishable to
/// the caller, so entity ids cannot be probed across tenants.
/// </remarks>
public sealed class TenantGuard : ITenantGuard
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ICurrentUserContext _currentUser;

    public TenantGuard(ApplicationDbContext dbContext, ICurrentUserContext currentUser)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
    }

    public async Task EnsureBranchInTenantAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        if (branchId == Guid.Empty)
        {
            throw new ArgumentException("BranchId is required.", nameof(branchId));
        }

        var organizationId = _currentUser.OrganizationId;
        var exists = await _dbContext.Branches
            .AsNoTracking()
            .AnyAsync(x => x.Id == branchId && x.OrganizationId == organizationId, cancellationToken);

        if (!exists)
        {
            throw new TenantAccessDeniedException(
                "The requested branch does not exist within the authenticated organization.");
        }

        _currentUser.EnsureBranchAccess(branchId);
    }

    public async Task<Guid> EnsureSessionInTenantAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("SessionId is required.", nameof(sessionId));
        }

        var organizationId = _currentUser.OrganizationId;
        var branchId = await _dbContext.Sessions
            .AsNoTracking()
            .Where(x => x.Id == sessionId && x.OrganizationId == organizationId)
            .Select(x => (Guid?)x.BranchId)
            .SingleOrDefaultAsync(cancellationToken);

        if (branchId is null)
        {
            throw new TenantAccessDeniedException(
                "The requested session does not exist within the authenticated organization.");
        }

        _currentUser.EnsureBranchAccess(branchId.Value);
        return branchId.Value;
    }

    public async Task EnsureDeviceInTenantAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("DeviceId is required.", nameof(deviceId));
        }

        var organizationId = _currentUser.OrganizationId;
        var branchId = await _dbContext.Devices
            .AsNoTracking()
            .Where(x => x.Id == deviceId && x.OrganizationId == organizationId)
            .Select(x => (Guid?)x.BranchId)
            .SingleOrDefaultAsync(cancellationToken);

        if (branchId is null)
        {
            throw new TenantAccessDeniedException(
                "The requested device does not exist within the authenticated organization.");
        }

        _currentUser.EnsureBranchAccess(branchId.Value);
    }

    public async Task<Guid> ResolveWritableBranchAsync(Guid? requestedBranchId, CancellationToken cancellationToken = default)
    {
        if (requestedBranchId is { } requested && requested != Guid.Empty)
        {
            await EnsureBranchInTenantAsync(requested, cancellationToken);
            return requested;
        }

        // Branch-scoped staff fall back to their own branch rather than being asked to
        // supply one, which removes any opportunity to name someone else's branch.
        if (_currentUser.BranchId is { } assigned && assigned != Guid.Empty)
        {
            await EnsureBranchInTenantAsync(assigned, cancellationToken);
            return assigned;
        }

        throw new ArgumentException(
            "A branch must be specified for this operation because the caller has no assigned branch.");
    }
}
