using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zazi.Application;
using Zazi.Application.Sync;
using Zazi.Application.Security;
using Zazi.Domain;

namespace Zazi.Infrastructure.Services;

/// <summary>
/// Issues, revokes and redeems device enrolment codes, and reports a device's own state.
/// </summary>
public sealed class DeviceEnrollmentService : IDeviceEnrollmentService
{
    /// <summary>
    /// 160 bits of entropy, rendered as 32 Crockford base32 characters in five groups.
    /// Sized so guessing is infeasible even without the attempt limit, and grouped so it can
    /// be read down a phone line without transcription errors.
    /// </summary>
    private const int CodeEntropyBytes = 20;

    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const string CodePrefixMarker = "CARL";
    private const int DisplayPrefixLength = 9;

    private readonly ApplicationDbContext _dbContext;
    private readonly SyncOptions _syncOptions;
    private readonly ILogger<DeviceEnrollmentService> _logger;

    public DeviceEnrollmentService(
        ApplicationDbContext dbContext,
        IOptions<SyncOptions> syncOptions,
        ILogger<DeviceEnrollmentService> logger)
    {
        _dbContext = dbContext;
        _syncOptions = syncOptions.Value;
        _logger = logger;
    }

    public async Task<EnrollmentCodeIssuedDto> IssueCodeAsync(
        IssueEnrollmentCodeRequest request,
        Guid organizationId,
        Guid branchId,
        Guid issuedByUserId,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.DeviceRole))
        {
            throw new ArgumentException("Unsupported device role.", nameof(request));
        }

        var lifetimeHours = request.LifetimeHours ?? (int)DeviceEnrollmentCode.DefaultLifetime.TotalHours;
        if (lifetimeHours is < 1 or > 168)
        {
            // A code that never expires is a permanent backdoor into a tenant.
            throw new ArgumentException(
                "Enrolment code lifetime must be between 1 and 168 hours.", nameof(request));
        }

        if (request.IntendedUserId is { } intendedUserId)
        {
            var userBelongs = await _dbContext.Users
                .AnyAsync(x => x.Id == intendedUserId && x.OrganizationId == organizationId, cancellationToken);

            if (!userBelongs)
            {
                throw new ArgumentException("The intended user is not in this organization.", nameof(request));
            }
        }

        var plaintext = GenerateCode();

        var entity = new DeviceEnrollmentCode
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            CodeHash = HashCode(plaintext),
            CodePrefix = plaintext[..DisplayPrefixLength],
            DeviceRole = request.DeviceRole,
            IntendedUserId = request.IntendedUserId,
            IssuedByUserId = issuedByUserId,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(lifetimeHours),
            Status = DeviceEnrollmentCodeStatus.Active,
            Label = request.Label
        };

        _dbContext.DeviceEnrollmentCodes.Add(entity);
        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = organizationId,
            UserId = issuedByUserId,
            Action = "DEVICE_ENROLLMENT_CODE_ISSUED",
            // The prefix identifies which code without disclosing anything usable.
            Details = $"Code {entity.CodePrefix}… issued for branch {branchId}, role {request.DeviceRole}, " +
                      $"expiring {entity.ExpiresAtUtc:O}.",
            ActorType = "User"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Enrolment code {CodePrefix} issued for organization {OrganizationId} branch {BranchId} role {DeviceRole}",
            entity.CodePrefix, organizationId, branchId, request.DeviceRole);

        // The only moment the plaintext exists outside the caller's hand.
        return new EnrollmentCodeIssuedDto(
            entity.Id, plaintext, entity.CodePrefix, organizationId, branchId,
            entity.DeviceRole, entity.ExpiresAtUtc, entity.Label);
    }

    public async Task<IReadOnlyList<EnrollmentCodeDto>> GetCodesAsync(
        Guid organizationId,
        Guid? branchId,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.DeviceEnrollmentCodes
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId);

        if (branchId is { } branch)
        {
            query = query.Where(x => x.BranchId == branch);
        }

        // Projected field by field so CodeHash cannot leak through a careless mapping.
        return await query
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new EnrollmentCodeDto(
                x.Id, x.CodePrefix, x.BranchId, x.DeviceRole, x.Status, x.ExpiresAtUtc,
                x.RedeemedAtUtc, x.RedeemedByDeviceId, x.FailedAttempts, x.Label, x.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task RevokeCodeAsync(
        Guid organizationId,
        Guid codeId,
        Guid revokedByUserId,
        CancellationToken cancellationToken = default)
    {
        var code = await _dbContext.DeviceEnrollmentCodes
            .SingleOrDefaultAsync(x => x.Id == codeId && x.OrganizationId == organizationId, cancellationToken)
            ?? throw new KeyNotFoundException("Enrolment code was not found.");

        if (code.Status != DeviceEnrollmentCodeStatus.Active)
        {
            throw new ConflictException($"The code is already {code.Status}.");
        }

        code.Status = DeviceEnrollmentCodeStatus.Revoked;
        code.RevokedAtUtc = DateTimeOffset.UtcNow;
        code.RevokedByUserId = revokedByUserId;
        code.UpdatedAt = DateTimeOffset.UtcNow;

        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = organizationId,
            UserId = revokedByUserId,
            Action = "DEVICE_ENROLLMENT_CODE_REVOKED",
            Details = $"Code {code.CodePrefix}… revoked before redemption.",
            ActorType = "User"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<DeviceEnrolledDto> RedeemAsync(
        RedeemEnrollmentCodeRequest request,
        Guid organizationId,
        Guid redeemingUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            throw new ArgumentException("An enrolment code is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.DeviceIdentifier))
        {
            throw new ArgumentException("A device identifier is required.", nameof(request));
        }

        var normalized = NormalizeCode(request.Code);
        var hash = HashCode(normalized);
        var now = DateTimeOffset.UtcNow;

        // Looked up by hash and scoped to the caller's organization: a code issued in one
        // tenant can never be redeemed from another.
        var code = await _dbContext.DeviceEnrollmentCodes
            .SingleOrDefaultAsync(
                x => x.CodeHash == hash && x.OrganizationId == organizationId, cancellationToken);

        if (code is null)
        {
            // Deliberately identical to the rejection below: a distinct "no such code"
            // response would let an attacker probe which codes exist.
            await RecordFailedAttemptAsync(organizationId, redeemingUserId, null, cancellationToken);
            throw new UnauthorizedAccessException("The enrolment code is not valid.");
        }

        if (!code.IsRedeemable(now))
        {
            code.FailedAttempts++;
            await RecordFailedAttemptAsync(organizationId, redeemingUserId, code, cancellationToken);
            throw new UnauthorizedAccessException("The enrolment code is not valid.");
        }

        if (code.IntendedUserId is { } intendedUserId && intendedUserId != redeemingUserId)
        {
            code.FailedAttempts++;
            await RecordFailedAttemptAsync(organizationId, redeemingUserId, code, cancellationToken);
            throw new UnauthorizedAccessException("The enrolment code is not valid.");
        }

        var existingDevice = await _dbContext.Devices
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.DeviceIdentifier == request.DeviceIdentifier,
                cancellationToken);

        if (existingDevice is not null)
        {
            // Re-enrolling an existing identifier would silently re-admit a revoked handset,
            // turning a deliberate revocation into a formality.
            throw new ConflictException("A device with this identifier is already registered.");
        }

        var device = new Device
        {
            OrganizationId = organizationId,
            BranchId = code.BranchId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Enrolled device" : request.Name.Trim(),
            DeviceIdentifier = request.DeviceIdentifier.Trim(),
            Platform = string.IsNullOrWhiteSpace(request.Platform) ? "Android" : request.Platform,
            // Derived from the platform string rather than accepted as a separate client
            // field: a handset that could name its own DeviceType could grant itself
            // capabilities, including SMS capture.
            DeviceType = DeviceTypeMapping.FromPlatformString(request.Platform),
            Network = string.IsNullOrWhiteSpace(request.Network) ? "MTN" : request.Network,
            // Scope comes from the code, never from the enrolling handset.
            Role = code.DeviceRole,
            Status = DeviceStatus.Active,
            AppVersion = request.AppVersion,
            OsVersion = request.OsVersion,
            LastSeenAt = now
        };

        // Claim the code atomically before creating anything.
        //
        // The checks above are advisory: two concurrent redemptions can both pass
        // IsRedeemable before either commits. Nothing else stops them — each handset supplies
        // a different device identifier, so the unique index on (OrganizationId,
        // DeviceIdentifier) never fires, and one code would admit several devices.
        //
        // This conditional UPDATE is the real guarantee. Exactly one caller can transition
        // the row out of Active; everyone else sees zero rows affected and is refused.
        if (_dbContext.Database.IsRelational())
        {
            var claimed = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE "DeviceEnrollmentCodes"
                 SET "Status" = 1, "RedeemedAtUtc" = {now}, "RedeemedByDeviceId" = {device.Id},
                     "UpdatedAt" = {now}
                 WHERE "Id" = {code.Id} AND "Status" = 0 AND "RedeemedAtUtc" IS NULL
                 """,
                cancellationToken);

            if (claimed == 0)
            {
                // Another request won the race.
                _dbContext.ChangeTracker.Clear();
                throw new UnauthorizedAccessException("The enrolment code is not valid.");
            }

            // The row was changed outside the tracker; drop the stale copy so SaveChanges
            // does not write it back over the claim.
            _dbContext.Entry(code).State = EntityState.Detached;
        }
        else
        {
            code.Status = DeviceEnrollmentCodeStatus.Redeemed;
            code.RedeemedAtUtc = now;
            code.RedeemedByDeviceId = device.Id;
            code.UpdatedAt = now;
        }

        _dbContext.Devices.Add(device);
        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = organizationId,
            UserId = redeemingUserId,
            DeviceId = device.Id,
            Action = "DEVICE_ENROLLED",
            Details = $"Device enrolled into branch {code.BranchId} as {code.DeviceRole} " +
                      $"using code {code.CodePrefix}….",
            ActorType = "User"
        });

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Two handsets raced on the same code or the same identifier. The unique indexes
            // decide; the loser is told plainly rather than silently getting a second device.
            _dbContext.ChangeTracker.Clear();
            throw new ConflictException(
                "The enrolment could not be completed because the code or device identifier was already used.");
        }

        _logger.LogInformation(
            "Device {DeviceId} enrolled into organization {OrganizationId} branch {BranchId} via code {CodePrefix}",
            device.Id, organizationId, code.BranchId, code.CodePrefix);

        return new DeviceEnrolledDto(
            device.Id, organizationId, device.BranchId, device.Name, device.Role, device.Status, now);
    }

    public async Task<DeviceSelfDto?> GetDeviceSelfAsync(
        Guid organizationId,
        string deviceIdentifier,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceIdentifier))
        {
            return null;
        }

        var device = await _dbContext.Devices
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.DeviceIdentifier == deviceIdentifier,
                cancellationToken);

        if (device is null)
        {
            return null;
        }

        var lastSync = await _dbContext.AuthSessions
            .AsNoTracking()
            .Where(x => x.DeviceId == device.Id)
            .OrderByDescending(x => x.LastSeenAtUtc)
            .Select(x => (DateTimeOffset?)x.LastSeenAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var isRevoked = device.IsRevoked
            || device.Status is DeviceStatus.Revoked or DeviceStatus.Quarantined;

        var platformCapabilities = PlatformCapabilityPolicy.For(device.DeviceType, isRevoked);

        return new DeviceSelfDto(
            device.Id,
            device.OrganizationId,
            device.BranchId,
            device.Name,
            device.DeviceIdentifier,
            device.Role,
            device.Status,
            isRevoked,
            device.Platform,
            device.Network,
            device.AppVersion,
            device.OsVersion,
            device.LastSeenAt,
            lastSync,
            CapabilitiesFor(device, isRevoked),
            // Server time lets a client detect its own clock drift instead of silently
            // stamping transactions with a wrong local time.
            DateTimeOffset.UtcNow)
        {
            DeviceType = device.DeviceType,
            PlatformCapabilities = platformCapabilities,
            CanCaptureSms = PlatformCapabilityPolicy.CanCaptureSms(device.DeviceType, isRevoked),
            // A revoked device is told nothing about how to sync; it has no authority to.
            SyncConfiguration = isRevoked
                ? null
                : new DeviceSyncConfigurationDto(
                    _syncOptions.MaxBatchSize,
                    (int)_syncOptions.MaxClockSkewAhead.TotalSeconds,
                    (int)_syncOptions.MaxBacklogAge.TotalDays)
        };
    }

    /// <summary>
    /// What this device may currently do. A revoked device is granted nothing, so a client
    /// can act on a single machine-readable list rather than re-deriving the rules.
    /// </summary>
    private static IReadOnlyList<string> CapabilitiesFor(Device device, bool isRevoked)
    {
        if (isRevoked)
        {
            return [];
        }

        var capabilities = new List<string> { "sync.submit", "session.participate" };

        if (device.Role is DeviceRole.TransactionDevice or DeviceRole.Hub)
        {
            capabilities.Add("sms.capture");
        }

        if (device.Role is DeviceRole.Hub)
        {
            capabilities.Add("gateway.relay");
        }

        return capabilities;
    }

    private async Task RecordFailedAttemptAsync(
        Guid organizationId,
        Guid userId,
        DeviceEnrollmentCode? code,
        CancellationToken cancellationToken)
    {
        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = organizationId,
            UserId = userId,
            Action = "DEVICE_ENROLLMENT_FAILED",
            // Never the submitted code, not even partially: a failed attempt log would
            // otherwise accumulate near-miss guesses.
            Details = code is null
                ? "Enrolment attempted with an unrecognised code."
                : $"Enrolment rejected for code {code.CodePrefix}… (status {code.Status}, " +
                  $"attempts {code.FailedAttempts}).",
            ActorType = "User"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Crockford base32, grouped for readability: CARL-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX.</summary>
    private static string GenerateCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(CodeEntropyBytes);
        var builder = new StringBuilder(CodePrefixMarker);

        for (var index = 0; index < bytes.Length; index++)
        {
            if (index % 2 == 0)
            {
                builder.Append('-');
            }

            builder.Append(CodeAlphabet[bytes[index] >> 3]);
            builder.Append(CodeAlphabet[((bytes[index] & 0b111) << 2) | (index % 4)]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Uppercases and strips separators so a code typed with different spacing still matches.
    /// </summary>
    private static string NormalizeCode(string code) =>
        new(code.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// SHA-256 over the normalised code. A fast hash is appropriate here — unlike a password,
    /// this is 160 bits of machine-generated entropy with a 24-hour life and an attempt
    /// limit, so there is nothing to brute-force.
    /// </summary>
    private static string HashCode(string code)
    {
        var normalized = NormalizeCode(code);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException?.GetType().GetProperty("SqlState")?.GetValue(exception.InnerException) as string == "23505";
}
