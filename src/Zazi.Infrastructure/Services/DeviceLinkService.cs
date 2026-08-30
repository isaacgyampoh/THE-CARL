using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Domain;

namespace Zazi.Infrastructure.Services;

public class DeviceLinkService : IDeviceLinkService
{
    private readonly ApplicationDbContext _dbContext;

    public DeviceLinkService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<DeviceLinkDto> LinkDevicesAsync(Guid organizationId, Guid branchId, Guid sourceDeviceId, Guid linkedDeviceId, string linkType, CancellationToken cancellationToken = default)
    {
        var sourceDevice = await _dbContext.Devices.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.BranchId == branchId && x.Id == sourceDeviceId, cancellationToken);
        var linkedDevice = await _dbContext.Devices.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.BranchId == branchId && x.Id == linkedDeviceId, cancellationToken);

        if (sourceDevice is null || linkedDevice is null)
        {
            throw new InvalidOperationException("Both source and linked devices must exist within the same organization and branch.");
        }

        var existing = await _dbContext.DeviceLinks
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.SourceDeviceId == sourceDeviceId && x.LinkedDeviceId == linkedDeviceId, cancellationToken);

        if (existing is not null)
        {
            existing.Status = "Active";
            existing.LinkType = string.IsNullOrWhiteSpace(linkType) ? existing.LinkType : linkType;
            existing.LinkedAtUtc = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Map(existing);
        }

        var link = new DeviceLink
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            SourceDeviceId = sourceDeviceId,
            LinkedDeviceId = linkedDeviceId,
            LinkType = string.IsNullOrWhiteSpace(linkType) ? "ApprovedCompanion" : linkType,
            Status = "Active",
            LinkedAtUtc = DateTimeOffset.UtcNow
        };

        _dbContext.DeviceLinks.Add(link);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(link);
    }

    public async Task<IReadOnlyList<DeviceLinkDto>> GetDeviceLinksAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.DeviceLinks
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.LinkedAtUtc)
            .Select(x => new DeviceLinkDto(
                x.Id,
                x.OrganizationId,
                x.BranchId,
                x.SourceDeviceId,
                x.LinkedDeviceId,
                x.LinkType,
                x.Status,
                x.LinkedAtUtc))
            .ToListAsync(cancellationToken);
    }

    private static DeviceLinkDto Map(DeviceLink link) => new(
        link.Id,
        link.OrganizationId,
        link.BranchId,
        link.SourceDeviceId,
        link.LinkedDeviceId,
        link.LinkType,
        link.Status,
        link.LinkedAtUtc);
}
