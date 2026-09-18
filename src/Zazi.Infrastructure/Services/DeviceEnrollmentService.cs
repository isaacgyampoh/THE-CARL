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
    /// 80 bits of entropy, rendered as twenty Crockford base32 characters in five groups.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was 160 bits, which produced a fifty-four character code. An agent is given that
    /// code over the phone or in a message and types it into a handset, and a code that long
    /// is transcribed wrongly often enough to matter — the cost of the extra entropy was paid
    /// entirely by the person least able to absorb it.
    /// </para>
    /// <para>
    /// 80 bits is still far beyond reach. The authenticated enrolment path additionally
    /// requires a caller identity; the anonymous activation path does not, and deliberately
    /// leans on the remaining controls instead — rate limiting to ten attempts a minute per
    /// address, single use, expiry, and a per-code failed-attempt counter. That caps guessing
    /// at roughly fourteen thousand attempts a day against a space of 2^80, which is not a
    /// contest. The limit on this code has never been its length.
    ///
    /// Losing the authentication requirement is a real reduction and is recorded as one. It
    /// is accepted because the alternative — making every worker hold an account purely to
    /// redeem a code — is the obstacle the activation flow exists to remove.
    /// </para>
    /// <para>
    /// Note that each byte yields two characters but only eight bits: the second character
    /// mixes in the group index, so the encoding is deliberately not dense. The entropy
    /// figure above is bytes times eight, not characters times five.
    /// </para>
    /// </remarks>
    private const int CodeEntropyBytes = 10;

    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const string CodePrefixMarker = "ZAZI";
    private const int DisplayPrefixLength = 9;

    private readonly ApplicationDbContext _dbContext;
    private readonly SyncOptions _syncOptions;
    private readonly ILogger<DeviceEnrollmentService> _logger;
    private readonly IAuthService _authService;

    public DeviceEnrollmentService(
        ApplicationDbContext dbContext,
        IOptions<SyncOptions> syncOptions,
        ILogger<DeviceEnrollmentService> logger,
        IAuthService authService)
    {
        _dbContext = dbContext;
        _syncOptions = syncOptions.Value;
        _logger = logger;
        _authService = authService;
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
            var intended = await _dbContext.Users
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.Id == intendedUserId && x.OrganizationId == organizationId, cancellationToken);

            if (intended is null)
            {
                throw new ArgumentException("The intended user is not in this organization.", nameof(request));
            }

            // The code's branch and the worker's branch must be the same one.
            //
            // Both values are legitimate on their own — the worker really is in their branch,
            // and an organization-wide issuer really may write to another — so nothing else
            // compares them. Left unchecked, activation produces a worker whose access token
            // claims one branch while the device recording their transactions sits in
            // another, so their authorisation and their financial records disagree about
            // where they work. Refused here so the owner finds out while issuing rather than
            // when the worker cannot use the code.
            if (intended.BranchId is { } workerBranch && workerBranch != branchId)
            {
                throw new ArgumentException(
                    "The intended user belongs to a different branch. Issue the code for their own branch.",
                    nameof(request));
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

    public async Task<DeviceActivationResult> ActivateAsync(
        ActivateDeviceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            throw new ArgumentException("An activation code is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.DeviceIdentifier))
        {
            throw new ArgumentException("A device identifier is required.", nameof(request));
        }

        var hash = HashCode(NormalizeCode(request.Code));
        var now = DateTimeOffset.UtcNow;

        // Looked up globally rather than within a tenant, because there is no caller to take
        // a tenant from — that is the whole difference between this and RedeemAsync. It is
        // safe because the lookup key is a SHA-256 of eighty bits of entropy: there is no
        // space to enumerate, and a miss reveals only that this particular string is not a
        // code. The organization is then read from the row, never from the request.
        var code = await _dbContext.DeviceEnrollmentCodes
            .SingleOrDefaultAsync(x => x.CodeHash == hash, cancellationToken);

        if (code is null)
        {
            // Identical to every other rejection below. A distinct "no such code" would turn
            // this endpoint into an oracle for which codes exist.
            _logger.LogWarning("Activation attempt with an unrecognised code.");
            throw new UnauthorizedAccessException("The activation code is not valid.");
        }

        if (!code.IsRedeemable(now))
        {
            code.FailedAttempts++;
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw new UnauthorizedAccessException("The activation code is not valid.");
        }

        // Activation requires a code bound to a worker. A code without one carries no
        // identity, so there is nobody to issue a session to; those codes remain valid on the
        // authenticated enrolment path, where the caller supplies the identity instead.
        if (code.IntendedUserId is not { } workerId)
        {
            code.FailedAttempts++;
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(
                "Activation attempted with code {CodePrefix}… which is not bound to a worker.",
                code.CodePrefix);
            throw new UnauthorizedAccessException("The activation code is not valid.");
        }

        var worker = await _dbContext.Users
            .SingleOrDefaultAsync(
                x => x.Id == workerId && x.OrganizationId == code.OrganizationId, cancellationToken);

        // A disabled worker cannot activate, and a code outliving the worker it names is
        // refused rather than quietly reassigned.
        //
        // The branch comparison is the enforcement rather than the convenience: IssueCodeAsync
        // refuses to create a mismatched code, but a code issued before that rule existed is
        // still in the database, and this is what stops it being redeemed into an
        // inconsistent state.
        if (worker is null || !worker.IsActive
            || (worker.BranchId is { } workerBranch && workerBranch != code.BranchId))
        {
            code.FailedAttempts++;
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw new UnauthorizedAccessException("The activation code is not valid.");
        }

        var existingDevice = await _dbContext.Devices
            .SingleOrDefaultAsync(
                x => x.OrganizationId == code.OrganizationId
                     && x.DeviceIdentifier == request.DeviceIdentifier,
                cancellationToken);

        if (existingDevice is not null)
        {
            // Re-activating a known identifier would re-admit a revoked handset and make
            // revocation a formality. Same rule as enrolment.
            throw new ConflictException("A device with this identifier is already registered.");
        }

        var device = new Device
        {
            OrganizationId = code.OrganizationId,
            BranchId = code.BranchId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Activated device" : request.Name.Trim(),
            DeviceIdentifier = request.DeviceIdentifier.Trim(),
            Platform = string.IsNullOrWhiteSpace(request.Platform) ? "Android" : request.Platform,
            DeviceType = DeviceTypeMapping.FromPlatformString(request.Platform),
            Network = string.IsNullOrWhiteSpace(request.Network) ? "MTN" : request.Network,
            // Every scoped value comes from the code. The handset describes only itself.
            Role = code.DeviceRole,
            Status = DeviceStatus.Active,
            AppVersion = request.AppVersion,
            OsVersion = request.OsVersion,
            LastSeenAt = now
        };

        // One transaction across the claim, the device and the session. Without it a crash
        // between claiming the code and issuing the session would burn the code and leave the
        // worker unable to activate or retry.
        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        // Exactly the guarantee RedeemAsync relies on: the checks above are advisory, and two
        // handsets can both pass them before either commits. Only one caller can move the row
        // out of Active, and the loser is refused.
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
                _dbContext.ChangeTracker.Clear();
                throw new UnauthorizedAccessException("The activation code is not valid.");
            }

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
            OrganizationId = code.OrganizationId,
            UserId = worker.Id,
            DeviceId = device.Id,
            Action = "DEVICE_ACTIVATED",
            Details = $"Device activated into branch {code.BranchId} as {code.DeviceRole} " +
                      $"using code {code.CodePrefix}….",
            ActorType = "User"
        });

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            _dbContext.ChangeTracker.Clear();
            throw new ConflictException(
                "The activation could not be completed because the code or device identifier was already used.");
        }

        var session = await _authService.IssueActivationSessionAsync(worker.Id, device.Id, cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        // Names for the confirmation screen. Read after the commit and never from the
        // request: the worker is told who the server thinks they are.
        var branchName = await _dbContext.Branches
            .AsNoTracking()
            .Where(x => x.Id == code.BranchId)
            .Select(x => x.Name)
            .SingleOrDefaultAsync(cancellationToken) ?? string.Empty;

        var organizationName = await _dbContext.Organizations
            .AsNoTracking()
            .Where(x => x.Id == code.OrganizationId)
            .Select(x => x.Name)
            .SingleOrDefaultAsync(cancellationToken) ?? string.Empty;

        // Never logs the code itself — only the non-secret display prefix.
        _logger.LogInformation(
            "Device {DeviceId} activated for worker {UserId} in organization {OrganizationId} " +
            "branch {BranchId} via code {CodePrefix}",
            device.Id, worker.Id, code.OrganizationId, code.BranchId, code.CodePrefix);

        return new DeviceActivationResult(
            session,
            device.Id,
            device.Name,
            code.BranchId,
            branchName,
            organizationName,
            worker.FullName);
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

        // Read separately rather than through a navigation property: the device query is
        // AsNoTracking and projecting a join here would pull the whole branch row for one
        // string. Null when the branch has somehow gone, which the client renders as absent.
        var branchName = await _dbContext.Branches
            .AsNoTracking()
            .Where(x => x.Id == device.BranchId)
            .Select(x => x.Name)
            .SingleOrDefaultAsync(cancellationToken);

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
            BranchName = branchName,
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

    /// <summary>Crockford base32, grouped for readability: ZAZI-XXXX-XXXX-XXXX-XXXX-XXXX.</summary>
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
