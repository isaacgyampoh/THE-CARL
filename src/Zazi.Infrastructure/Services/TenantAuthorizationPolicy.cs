using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Domain;

namespace Zazi.Infrastructure.Services;

public class TenantAuthorizationPolicy : IAuthorizationPolicy
{
    private readonly ApplicationDbContext _dbContext;

    public TenantAuthorizationPolicy(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public bool CanViewOrganization(Guid userId, Guid organizationId)
    {
        return _dbContext.Users
            .AsNoTracking()
            .Any(x => x.Id == userId && x.OrganizationId == organizationId && x.IsActive);
    }

    public bool CanManageBranch(Guid userId, Guid branchId)
    {
        var user = _dbContext.Users
            .AsNoTracking()
            .Include(x => x.Roles)
            .SingleOrDefault(x => x.Id == userId && x.IsActive);

        if (user is null)
        {
            return false;
        }

        var roles = user.Roles.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var branchMatches = user.BranchId == branchId;
        return branchMatches || roles.Contains("OrganizationOwner") || roles.Contains("OrganizationAdmin") || roles.Contains("BranchManager");
    }

    public bool CanRecordTransaction(Guid userId, Guid branchId)
    {
        var user = _dbContext.Users
            .AsNoTracking()
            .Include(x => x.Roles)
            .SingleOrDefault(x => x.Id == userId && x.IsActive);

        if (user is null)
        {
            return false;
        }

        var roles = user.Roles.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return user.BranchId == branchId || roles.Contains("BranchManager") || roles.Contains("Agent") || roles.Contains("OrganizationAdmin");
    }

    public bool CanCloseSession(Guid userId, Guid sessionId)
    {
        var user = _dbContext.Users
            .AsNoTracking()
            .Include(x => x.Roles)
            .SingleOrDefault(x => x.Id == userId && x.IsActive);

        if (user is null)
        {
            return false;
        }

        var session = _dbContext.Sessions
            .AsNoTracking()
            .SingleOrDefault(x => x.Id == sessionId);

        if (session is null)
        {
            return false;
        }

        var roles = user.Roles.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return session.UserId == userId || roles.Contains("BranchManager") || roles.Contains("OrganizationAdmin") || roles.Contains("OrganizationOwner");
    }
}
