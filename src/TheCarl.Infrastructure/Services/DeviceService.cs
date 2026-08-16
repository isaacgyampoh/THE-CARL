using Microsoft.EntityFrameworkCore;
using TheCarl.Application;
using TheCarl.Domain;

namespace TheCarl.Infrastructure.Services;

public class DeviceService : IDeviceService
{
    private readonly ApplicationDbContext _dbContext;

    public DeviceService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<DeviceDto> RegisterDeviceAsync(CreateDeviceRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Device name is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.DeviceIdentifier))
        {
            throw new ArgumentException("Device identifier is required.", nameof(request));
        }

        var exists = await _dbContext.Devices
            .AnyAsync(x => x.OrganizationId == request.OrganizationId && x.DeviceIdentifier == request.DeviceIdentifier, cancellationToken);

        if (exists)
        {
            throw new InvalidOperationException("A device with this identifier is already registered for the organization.");
        }

        var device = new Device
        {
            OrganizationId = request.OrganizationId,
            BranchId = request.BranchId,
            Name = request.Name,
            DeviceIdentifier = request.DeviceIdentifier,
            Platform = request.Platform,
            Network = request.Network,
            Role = request.Role,
            Status = DeviceStatus.Active,
            AppVersion = request.AppVersion,
            OsVersion = request.OsVersion,
            LastSeenAt = DateTimeOffset.UtcNow
        };

        _dbContext.Devices.Add(device);
        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = request.OrganizationId,
            DeviceId = device.Id,
            Action = "DeviceRegistered",
            Details = $"Device {request.Name} registered for branch {request.BranchId}.",
            ActorType = "System"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new DeviceDto(
            device.Id,
            device.OrganizationId,
            device.BranchId,
            device.Name,
            device.DeviceIdentifier,
            device.Platform,
            device.Network,
            device.Role,
            device.Status,
            device.AppVersion,
            device.OsVersion,
            device.LastSeenAt);
    }

    public async Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Devices
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .Select(x => new DeviceDto(
                x.Id,
                x.OrganizationId,
                x.BranchId,
                x.Name,
                x.DeviceIdentifier,
                x.Platform,
                x.Network,
                x.Role,
                x.Status,
                x.AppVersion,
                x.OsVersion,
                x.LastSeenAt))
            .ToListAsync(cancellationToken);
    }
}
