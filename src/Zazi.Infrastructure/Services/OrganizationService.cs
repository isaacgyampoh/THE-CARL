using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Domain;

namespace Zazi.Infrastructure.Services;

public class OrganizationService : IOrganizationService
{
    private readonly ApplicationDbContext _dbContext;

    public OrganizationService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<OrganizationDto>> GetOrganizationsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Organizations
            .AsNoTracking()
            .Select(x => new OrganizationDto(
                x.Id,
                x.Name,
                x.Email,
                x.PhoneNumber,
                x.Country,
                x.CurrencyCode,
                x.CreatedAt,
                x.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<OrganizationDto> CreateOrganizationAsync(CreateOrganizationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Organization name is required.", nameof(request));
        }

        var entity = new Organization
        {
            Name = request.Name,
            Email = request.Email,
            PhoneNumber = request.PhoneNumber,
            Country = string.IsNullOrWhiteSpace(request.Country) ? "GH" : request.Country,
            CurrencyCode = string.IsNullOrWhiteSpace(request.CurrencyCode) ? Money.DefaultCurrency : request.CurrencyCode
        };

        _dbContext.Organizations.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new OrganizationDto(
            entity.Id,
            entity.Name,
            entity.Email,
            entity.PhoneNumber,
            entity.Country,
            entity.CurrencyCode,
            entity.CreatedAt,
            entity.UpdatedAt);
    }

    public async Task<BranchDto?> CreateBranchAsync(CreateBranchRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Branch name is required.", nameof(request));
        }

        var organization = await _dbContext.Organizations
            .SingleOrDefaultAsync(x => x.Id == request.OrganizationId, cancellationToken);

        if (organization is null)
        {
            return null;
        }

        var branch = new Branch
        {
            OrganizationId = request.OrganizationId,
            Name = request.Name,
            Location = request.Location
        };

        _dbContext.Branches.Add(branch);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new BranchDto(
            branch.Id,
            branch.OrganizationId,
            branch.Name,
            branch.Location,
            branch.CreatedAt,
            branch.UpdatedAt);
    }

    public async Task<IReadOnlyList<BranchDto>> GetBranchesAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Branches
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .Select(x => new BranchDto(
                x.Id,
                x.OrganizationId,
                x.Name,
                x.Location,
                x.CreatedAt,
                x.UpdatedAt))
            .ToListAsync(cancellationToken);
    }
}
