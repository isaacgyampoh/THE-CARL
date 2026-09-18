using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Domain;

namespace Zazi.Infrastructure.Services;

public class DeviceService : IDeviceService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IIdentityRevocationService _revocation;

    public DeviceService(ApplicationDbContext dbContext, IIdentityRevocationService revocation)
    {
        _dbContext = dbContext;
        _revocation = revocation;
    }

    public async Task RevokeDeviceAsync(
        Guid deviceId,
        Guid organizationId,
        Guid actorUserId,
        Guid? requiredBranchId = null,
        CancellationToken cancellationToken = default)
    {
        // Scoped to the caller's organization, so one business cannot revoke another's
        // handset by guessing an id — and, when the caller is branch-scoped, to that branch
        // too. Both conditions are in the query rather than checked afterwards, so a device
        // the caller may not touch is indistinguishable from one that does not exist.
        var device = await _dbContext.Devices
            .SingleOrDefaultAsync(
                x => x.Id == deviceId
                     && x.OrganizationId == organizationId
                     && (requiredBranchId == null || x.BranchId == requiredBranchId),
                cancellationToken)
            ?? throw new KeyNotFoundException("Device was not found.");

        device.IsRevoked = true;
        device.Status = DeviceStatus.Revoked;
        device.UpdatedAt = DateTimeOffset.UtcNow;

        // Marking the device is not enough on its own: the handset already holds a refresh
        // token, and without this it would keep minting access tokens until that token aged
        // out. Revoking a stolen phone has to mean it stops working at its next server
        // contact, not eventually.
        await _revocation.RevokeDeviceSessionsAsync(deviceId, actorUserId, cancellationToken);

        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = organizationId,
            UserId = actorUserId,
            DeviceId = deviceId,
            Action = "DEVICE_REVOKED",
            Details = "Device revoked by an administrator; bound sessions terminated.",
            ActorType = "User"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
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
