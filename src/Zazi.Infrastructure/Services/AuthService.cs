using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Domain;

namespace Zazi.Infrastructure.Services;

public class AuthService : IAuthService
{
    private const int MaxFailedLoginAttempts = 5;
    private const int LockoutMinutes = 15;

    // OWASP's 2023 guidance for PBKDF2-HMAC-SHA256 is 600,000 iterations.
    private const int Pbkdf2Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int DerivedKeyBytes = 32;

    private readonly ApplicationDbContext _dbContext;
    private readonly JwtOptions _jwtOptions;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        ApplicationDbContext dbContext,
        IOptions<JwtOptions> jwtOptions,
        ILogger<AuthService> logger)
    {
        _dbContext = dbContext;
        _jwtOptions = jwtOptions.Value;
        _logger = logger;
    }

    public async Task<UserDto> RegisterUserAsync(RegisterUserRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            throw new ArgumentException("Full name is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            throw new ArgumentException("Email is required.", nameof(request));
        }

        ValidatePasswordStrength(request.Password);

        var organizationExists = await _dbContext.Organizations
            .AnyAsync(x => x.Id == request.OrganizationId, cancellationToken);
        if (!organizationExists)
        {
            throw new KeyNotFoundException("Organization was not found.");
        }

        // Roles are normalized to the canonical set and unknown names are rejected outright.
        // Silently falling back to a default role on a typo would grant unintended access.
        var requestedRoles = request.Roles.Length == 0 ? [ZaziRoles.Agent] : request.Roles;
        var canonicalRoles = new List<string>();
        foreach (var supplied in requestedRoles)
        {
            var canonical = ZaziRoles.Normalize(supplied)
                ?? throw new ArgumentException($"'{supplied}' is not a recognised role.", nameof(request));
            if (!canonicalRoles.Contains(canonical, StringComparer.Ordinal))
            {
                canonicalRoles.Add(canonical);
            }
        }

        // A branch-scoped role without a branch cannot be authorized against anything.
        if (request.BranchId is null && canonicalRoles.Any(r => !ZaziRoles.IsOrganizationWide(r)))
        {
            throw new ArgumentException(
                "Branch-scoped roles require a BranchId.", nameof(request));
        }

        if (request.BranchId is { } branchId)
        {
            var branchBelongsToOrganization = await _dbContext.Branches
                .AnyAsync(x => x.Id == branchId && x.OrganizationId == request.OrganizationId, cancellationToken);
            if (!branchBelongsToOrganization)
            {
                throw new ArgumentException("The branch does not belong to the organization.", nameof(request));
            }
        }

        var normalizedEmail = NormalizeEmail(request.Email);
        var userExists = await _dbContext.Users
            .AnyAsync(x => x.OrganizationId == request.OrganizationId && x.Email == normalizedEmail, cancellationToken);
        if (userExists)
        {
            throw new ConflictException("A user with this email already exists in the organization.");
        }

        var passwordHash = HashPassword(request.Password, out var salt);
        var entity = new User
        {
            OrganizationId = request.OrganizationId,
            BranchId = request.BranchId,
            FullName = request.FullName.Trim(),
            Email = normalizedEmail,
            PhoneNumber = request.PhoneNumber,
            IsActive = true,
            EmailVerified = request.EmailVerified,
            PhoneVerified = request.PhoneVerified,
            PasswordHash = passwordHash,
            PasswordSalt = salt,
            SecurityStamp = NewSecurityStamp()
        };

        foreach (var roleName in canonicalRoles)
        {
            entity.Roles.Add(await EnsureRoleAsync(request.OrganizationId, roleName, cancellationToken));
        }

        _dbContext.Users.Add(entity);
        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = request.OrganizationId,
            UserId = entity.Id,
            Action = "STAFF_CREATED",
            Details = $"User created with roles {string.Join(", ", canonicalRoles)}.",
            ActorType = "System"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return MapUser(entity, canonicalRoles);
    }

    public async Task<UserDto> CreateWorkerAsync(
        CreateWorkerRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            throw new ArgumentException("Full name is required.", nameof(request));
        }

        // A worker is always branch-scoped. An organization-wide worker is a contradiction:
        // the point of the role is that it sees one branch's data.
        var branchBelongsToOrganization = await _dbContext.Branches
            .AnyAsync(
                x => x.Id == request.BranchId && x.OrganizationId == request.OrganizationId,
                cancellationToken);
        if (!branchBelongsToOrganization)
        {
            throw new ArgumentException("The branch does not belong to the organization.", nameof(request));
        }

        // Same normalisation and same rejection of unknown names as RegisterUserAsync. A typo
        // must not quietly fall back to a default role.
        var requestedRoles = request.Roles.Length == 0 ? [ZaziRoles.Agent] : request.Roles;
        var canonicalRoles = new List<string>();
        foreach (var supplied in requestedRoles)
        {
            var canonical = ZaziRoles.Normalize(supplied)
                ?? throw new ArgumentException($"'{supplied}' is not a recognised role.", nameof(request));
            if (!canonicalRoles.Contains(canonical, StringComparer.Ordinal))
            {
                canonicalRoles.Add(canonical);
            }
        }

        // An organization-wide role would escape the branch scoping above and hand a worker
        // the whole business.
        if (canonicalRoles.Any(ZaziRoles.IsOrganizationWide))
        {
            throw new ArgumentException(
                "A worker cannot hold an organization-wide role.", nameof(request));
        }

        var entity = new User
        {
            OrganizationId = request.OrganizationId,
            BranchId = request.BranchId,
            FullName = request.FullName.Trim(),
            // No address, and no synthetic one. Null is the honest representation, and the
            // unique index tolerates any number of them.
            Email = null,
            PhoneNumber = request.PhoneNumber,
            CredentialType = UserCredentialType.ActivationOnly,
            IsActive = true,
            // Left empty deliberately. VerifyPassword refuses a blank hash, so this identity
            // cannot be reached by the password route at all.
            PasswordHash = string.Empty,
            PasswordSalt = string.Empty,
            SecurityStamp = NewSecurityStamp()
        };

        foreach (var roleName in canonicalRoles)
        {
            entity.Roles.Add(await EnsureRoleAsync(request.OrganizationId, roleName, cancellationToken));
        }

        _dbContext.Users.Add(entity);
        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = request.OrganizationId,
            UserId = entity.Id,
            Action = "WORKER_CREATED",
            Details = $"Worker created in branch {request.BranchId} with roles " +
                      $"{string.Join(", ", canonicalRoles)}. No password credential.",
            ActorType = "User"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return MapUser(entity, canonicalRoles);
    }

    public async Task<AuthTokenResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new UnauthorizedAccessException("Email and password are required.");
        }

        var normalizedEmail = NormalizeEmail(request.Email);

        // Email is only unique per organization, so a bare email lookup is ambiguous and
        // previously threw when two tenants shared an address. Candidates are enumerated and
        // the password is checked against each, which also keeps the failure path uniform.
        // Activation-only workers are excluded from the candidate set outright. They have no
        // address to match and no credential to verify, but stating it here makes the rule
        // explicit and independently testable rather than leaving it to emerge from an empty
        // hash further down. VerifyPassword's refusal of a blank hash remains the security
        // boundary; this is the second layer, and each is asserted on its own.
        var candidates = await _dbContext.Users
            .Include(x => x.Roles)
            .Where(x => x.Email == normalizedEmail && x.CredentialType == UserCredentialType.Password)
            .ToListAsync(cancellationToken);

        var user = candidates.FirstOrDefault(x => VerifyPassword(request.Password, x.PasswordHash, x.PasswordSalt));

        if (user is null)
        {
            // Record the failure against every candidate so lockout still applies, then return
            // an identical error regardless of whether the account exists.
            foreach (var candidate in candidates)
            {
                RegisterFailedAttempt(candidate);
            }

            if (candidates.Count > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            _logger.LogWarning("Failed login attempt for a {CandidateCount}-candidate email.", candidates.Count);
            throw new UnauthorizedAccessException("Invalid credentials.");
        }

        if (!user.IsActive)
        {
            throw new UnauthorizedAccessException("Invalid credentials.");
        }

        if (user.LockoutUntilUtc.HasValue && user.LockoutUntilUtc.Value > DateTimeOffset.UtcNow)
        {
            _dbContext.AuditLogs.Add(new AuditLogEntry
            {
                OrganizationId = user.OrganizationId,
                UserId = user.Id,
                Action = "LOGIN_BLOCKED_LOCKOUT",
                Details = "Login rejected while the account is locked out.",
                ActorType = "User"
            });
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw new UnauthorizedAccessException("Account is temporarily locked.");
        }

        user.FailedLoginAttempts = 0;
        user.LockoutUntilUtc = null;
        user.LastLoginAtUtc = DateTimeOffset.UtcNow;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        var roles = ResolveCanonicalRoles(user);

        // Resolve the device this login is bound to. A revoked device cannot start a session,
        // so a stolen credential on a decommissioned handset is dead on arrival.
        Guid? deviceId = null;
        if (!string.IsNullOrWhiteSpace(request.DeviceIdentifier))
        {
            var device = await _dbContext.Devices.SingleOrDefaultAsync(
                x => x.OrganizationId == user.OrganizationId && x.DeviceIdentifier == request.DeviceIdentifier,
                cancellationToken);

            if (device is null)
            {
                throw new UnauthorizedAccessException("The device is not registered to this organization.");
            }

            if (device.IsRevoked || device.Status is DeviceStatus.Revoked or DeviceStatus.Quarantined)
            {
                _dbContext.AuditLogs.Add(new AuditLogEntry
                {
                    OrganizationId = user.OrganizationId,
                    UserId = user.Id,
                    DeviceId = device.Id,
                    Action = "LOGIN_BLOCKED_DEVICE_REVOKED",
                    Details = "Sign-in refused because the device is revoked.",
                    ActorType = "System"
                });
                await _dbContext.SaveChangesAsync(cancellationToken);
                throw new UnauthorizedAccessException("The device is not permitted to sign in.");
            }

            device.LastSeenAt = DateTimeOffset.UtcNow;
            deviceId = device.Id;
        }

        var authSession = new AuthSession
        {
            UserId = user.Id,
            OrganizationId = user.OrganizationId,
            DeviceId = deviceId,
            FamilyId = Guid.NewGuid().ToString("N"),
            Status = AuthSessionStatus.Active,
            SecurityStampAtIssue = user.SecurityStamp,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(_jwtOptions.RefreshTokenDays),
            LastSeenAtUtc = DateTimeOffset.UtcNow
        };
        _dbContext.AuthSessions.Add(authSession);

        var accessToken = CreateAccessToken(user, roles);
        var (refreshToken, refreshEntity) = CreateRefreshToken(user, authSession.FamilyId, authSession.Id);
        _dbContext.RefreshTokens.Add(refreshEntity);

        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = user.OrganizationId,
            UserId = user.Id,
            Action = "LOGIN",
            Details = "User successfully authenticated.",
            ActorType = "User"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new AuthTokenResult(
            accessToken,
            refreshToken,
            DateTimeOffset.UtcNow.AddMinutes(_jwtOptions.AccessTokenMinutes),
            MapUser(user, roles));
    }

    public async Task<AuthTokenResult> IssueActivationSessionAsync(
        Guid userId,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.Users
            .Include(x => x.Roles)
            .SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new UnauthorizedAccessException("The activation could not be completed.");

        // Re-checked here even though the activation path checked it. This method issues a
        // session and must not depend on a caller having been careful.
        if (!user.IsActive)
        {
            throw new UnauthorizedAccessException("The activation could not be completed.");
        }

        var device = await _dbContext.Devices
            .SingleOrDefaultAsync(
                x => x.Id == deviceId && x.OrganizationId == user.OrganizationId, cancellationToken)
            ?? throw new UnauthorizedAccessException("The activation could not be completed.");

        if (device.IsRevoked || device.Status is DeviceStatus.Revoked or DeviceStatus.Quarantined)
        {
            throw new UnauthorizedAccessException("The activation could not be completed.");
        }

        var roles = ResolveCanonicalRoles(user);

        // The same objects login produces. Nothing downstream — refresh, revocation, the
        // security stamp check — needs to know this session began with a code rather than a
        // password, which is the point: one security model, not two.
        var authSession = new AuthSession
        {
            UserId = user.Id,
            OrganizationId = user.OrganizationId,
            DeviceId = device.Id,
            FamilyId = Guid.NewGuid().ToString("N"),
            Status = AuthSessionStatus.Active,
            SecurityStampAtIssue = user.SecurityStamp,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(_jwtOptions.RefreshTokenDays),
            LastSeenAtUtc = DateTimeOffset.UtcNow
        };
        _dbContext.AuthSessions.Add(authSession);

        var accessToken = CreateAccessToken(user, roles);
        var (refreshToken, refreshEntity) = CreateRefreshToken(user, authSession.FamilyId, authSession.Id);
        _dbContext.RefreshTokens.Add(refreshEntity);

        user.LastLoginAtUtc = DateTimeOffset.UtcNow;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        device.LastSeenAt = DateTimeOffset.UtcNow;

        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = user.OrganizationId,
            UserId = user.Id,
            DeviceId = device.Id,
            Action = "WORKER_ACTIVATED",
            Details = "Session issued by activation code redemption.",
            ActorType = "User"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new AuthTokenResult(
            accessToken,
            refreshToken,
            DateTimeOffset.UtcNow.AddMinutes(_jwtOptions.AccessTokenMinutes),
            MapUser(user, roles));
    }

    public async Task<AuthTokenResult> RefreshTokenAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            throw new UnauthorizedAccessException("Refresh token is required.");
        }

        var presentedHash = HashToken(request.RefreshToken);
        var existingToken = await _dbContext.RefreshTokens
            .SingleOrDefaultAsync(x => x.TokenHash == presentedHash, cancellationToken);

        if (existingToken is null)
        {
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");
        }

        // Presenting an already-rotated token means the token leaked: the legitimate client
        // holds the replacement. Revoke the entire family so the attacker and the victim are
        // both forced to re-authenticate.
        if (existingToken.IsRevoked)
        {
            await RevokeFamilyAsync(existingToken, "Refresh token reuse detected.", cancellationToken);
            _logger.LogWarning(
                "Refresh token reuse detected for user {UserId}; token family {FamilyId} revoked.",
                existingToken.UserId,
                existingToken.FamilyId);
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");
        }

        if (existingToken.IsExpired)
        {
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");
        }

        if (request.OrganizationId is { } requestedOrganizationId
            && requestedOrganizationId != existingToken.OrganizationId)
        {
            throw new TenantAccessDeniedException(existingToken.OrganizationId, requestedOrganizationId);
        }

        var user = await _dbContext.Users
            .Include(x => x.Roles)
            .SingleOrDefaultAsync(x => x.Id == existingToken.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            await RevokeFamilyAsync(existingToken, "User is no longer active.", cancellationToken);
            throw new UnauthorizedAccessException("User no longer active.");
        }

        // ─── Session, device and security-stamp validation ────────────────────
        // These three checks are what make revocation immediate. An access token stays
        // valid until it expires, but it cannot be renewed once any link in
        // User -> Device -> Session -> token family is broken, so the blast radius of a
        // revocation is bounded by the access-token lifetime rather than the refresh window.
        var authSession = existingToken.SessionId is { } sessionId
            ? await _dbContext.AuthSessions.SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken)
            : null;

        if (authSession is not null)
        {
            if (!authSession.IsActive)
            {
                await RevokeFamilyAsync(existingToken, $"Session is {authSession.Status}.", cancellationToken);
                throw new UnauthorizedAccessException("The session is no longer valid.");
            }

            if (authSession.DeviceId is { } boundDeviceId)
            {
                var device = await _dbContext.Devices
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == boundDeviceId, cancellationToken);

                if (device is null
                    || device.IsRevoked
                    || device.Status is DeviceStatus.Revoked or DeviceStatus.Quarantined)
                {
                    authSession.Revoke(AuthSessionStatus.RevokedByDeviceRevocation, "Bound device was revoked.");
                    await RevokeFamilyAsync(existingToken, "Bound device was revoked.", cancellationToken);
                    throw new UnauthorizedAccessException("The device is no longer permitted to refresh.");
                }
            }

            // A changed security stamp means the password changed, roles changed, or an
            // administrator revoked sessions. One indexed comparison, no cache required.
            if (!string.Equals(authSession.SecurityStampAtIssue, user.SecurityStamp, StringComparison.Ordinal))
            {
                authSession.Revoke(AuthSessionStatus.RevokedBySecurityChange, "Security stamp changed.");
                await RevokeFamilyAsync(existingToken, "Security stamp changed.", cancellationToken);
                throw new UnauthorizedAccessException("The session was invalidated by a security change.");
            }

            authSession.LastSeenAtUtc = DateTimeOffset.UtcNow;
        }

        var roles = ResolveCanonicalRoles(user);
        var (replacementToken, replacementEntity) = CreateRefreshToken(
            user, existingToken.FamilyId, authSession?.Id ?? existingToken.SessionId ?? Guid.Empty);

        existingToken.RevokedAtUtc = DateTimeOffset.UtcNow;
        existingToken.RevokedReason = "Rotated.";
        existingToken.ReplacedByTokenHash = replacementEntity.TokenHash;
        existingToken.UpdatedAt = DateTimeOffset.UtcNow;

        _dbContext.RefreshTokens.Add(replacementEntity);

        var accessToken = CreateAccessToken(user, roles);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new AuthTokenResult(
            accessToken,
            replacementToken,
            DateTimeOffset.UtcNow.AddMinutes(_jwtOptions.AccessTokenMinutes),
            MapUser(user, roles));
    }

    public async Task<IReadOnlyList<UserDto>> GetUsersAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId is required.", nameof(organizationId));
        }

        var users = await _dbContext.Users
            .AsNoTracking()
            .Include(x => x.Roles)
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.FullName)
            .ToListAsync(cancellationToken);

        return users.Select(user => MapUser(user, ResolveCanonicalRoles(user))).ToList();
    }

    private async Task RevokeFamilyAsync(RefreshToken token, string reason, CancellationToken cancellationToken)
    {
        var family = await _dbContext.RefreshTokens
            .Where(x => x.UserId == token.UserId && x.FamilyId == token.FamilyId && x.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var member in family)
        {
            member.RevokedAtUtc = DateTimeOffset.UtcNow;
            member.RevokedReason = reason;
            member.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // The session dies with its token family; otherwise a revoked family would leave an
        // active session record that later logic could treat as still trustworthy.
        var owningSession = await _dbContext.AuthSessions
            .SingleOrDefaultAsync(x => x.FamilyId == token.FamilyId, cancellationToken);
        if (owningSession is not null && owningSession.RevokedAtUtc is null)
        {
            owningSession.Revoke(AuthSessionStatus.RevokedByTokenReuse, reason);
        }

        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = token.OrganizationId,
            UserId = token.UserId,
            Action = "REFRESH_TOKEN_FAMILY_REVOKED",
            Details = reason,
            ActorType = "System"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private void RegisterFailedAttempt(User candidate)
    {
        candidate.FailedLoginAttempts += 1;
        if (candidate.FailedLoginAttempts >= MaxFailedLoginAttempts)
        {
            candidate.LockoutUntilUtc = DateTimeOffset.UtcNow.AddMinutes(LockoutMinutes);
            _dbContext.Alerts.Add(new AlertRecord
            {
                OrganizationId = candidate.OrganizationId,
                BranchId = candidate.BranchId ?? Guid.Empty,
                Type = "MULTIPLE_FAILED_LOGINS",
                Message = $"Account locked after {candidate.FailedLoginAttempts} consecutive failed sign-in attempts.",
                Severity = "High",
                Network = string.Empty
            });
        }

        candidate.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void ValidatePasswordStrength(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
        {
            throw new ArgumentException("Password must be at least 12 characters long.", nameof(password));
        }

        var categories = 0;
        if (password.Any(char.IsUpper)) categories++;
        if (password.Any(char.IsLower)) categories++;
        if (password.Any(char.IsDigit)) categories++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) categories++;

        if (categories < 3)
        {
            throw new ArgumentException(
                "Password must combine at least three of: uppercase, lowercase, digits, symbols.",
                nameof(password));
        }
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string NewSecurityStamp() => Guid.NewGuid().ToString("N");

    private static IReadOnlyList<string> ResolveCanonicalRoles(User user) =>
        user.Roles
            .Select(r => ZaziRoles.Normalize(r.Name))
            .Where(r => r is not null)
            .Select(r => r!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private async Task<Role> EnsureRoleAsync(Guid organizationId, string canonicalName, CancellationToken cancellationToken)
    {
        var role = await _dbContext.Roles
            .Include(x => x.Permissions)
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Name == canonicalName, cancellationToken);

        if (role is not null)
        {
            return role;
        }

        var newRole = new Role
        {
            OrganizationId = organizationId,
            Name = canonicalName,
            Description = $"{canonicalName} role for the organization."
        };

        foreach (var permissionName in GetDefaultPermissions(canonicalName))
        {
            newRole.Permissions.Add(new Permission
            {
                RoleId = newRole.Id,
                Name = permissionName,
                Description = $"Allows {permissionName} scoped to the tenant."
            });
        }

        _dbContext.Roles.Add(newRole);
        return newRole;
    }

    /// <summary>
    /// Permissions recorded against a role for audit and future fine-grained checks.
    /// Runtime authorization is driven by <see cref="ZaziPolicies.RolesByPolicy"/>; this
    /// keeps the two aligned by deriving from the same canonical role names.
    /// </summary>
    private static string[] GetDefaultPermissions(string canonicalRole) =>
        ZaziPolicies.RolesByPolicy
            .Where(entry => entry.Value.Contains(canonicalRole, StringComparer.Ordinal))
            .Select(entry => entry.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static string HashPassword(string password, out string salt)
    {
        salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltBytes));
        return HashPassword(password, salt);
    }

    private static string HashPassword(string password, string salt)
    {
        var bytes = KeyDerivation.Pbkdf2(
            password,
            Encoding.UTF8.GetBytes(salt),
            KeyDerivationPrf.HMACSHA256,
            Pbkdf2Iterations,
            DerivedKeyBytes);

        return Convert.ToBase64String(bytes);
    }

    private static bool VerifyPassword(string password, string passwordHash, string salt)
    {
        if (string.IsNullOrWhiteSpace(salt) || string.IsNullOrWhiteSpace(passwordHash))
        {
            return false;
        }

        byte[] expected;
        byte[] actual;
        try
        {
            expected = Convert.FromBase64String(passwordHash);
            actual = Convert.FromBase64String(HashPassword(password, salt));
        }
        catch (FormatException)
        {
            return false;
        }

        // Fixed-time comparison: a short-circuiting string compare leaks hash prefixes.
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private string CreateAccessToken(User user, IReadOnlyList<string> canonicalRoles)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.Key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ZaziClaimTypes.OrganizationId, user.OrganizationId.ToString()),
            new(ZaziClaimTypes.SecurityStamp, user.SecurityStamp),
            new("name", user.FullName)
        };

        // Omitted entirely for an activation-only worker rather than emitted empty. A claim
        // present but blank invites a consumer to treat "" as an identity; an absent claim
        // cannot be misread. Nothing authorises on this claim — it is for display.
        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim("email", user.Email));
        }

        if (user.BranchId is { } branchId)
        {
            claims.Add(new Claim(ZaziClaimTypes.BranchId, branchId.ToString()));
        }

        // One claim per role. Role-based authorization matches claim values individually,
        // so a single comma-joined value would match no role at all.
        foreach (var role in canonicalRoles)
        {
            claims.Add(new Claim(ZaziClaimTypes.Role, role));
        }

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(_jwtOptions.AccessTokenMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Returns the clear-text token (handed to the client exactly once) alongside the
    /// entity, which stores only the hash.
    /// </summary>
    private (string Token, RefreshToken Entity) CreateRefreshToken(User user, string familyId, Guid sessionId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var entity = new RefreshToken
        {
            UserId = user.Id,
            OrganizationId = user.OrganizationId,
            SessionId = sessionId,
            TokenHash = HashToken(token),
            FamilyId = familyId,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(_jwtOptions.RefreshTokenDays)
        };

        return (token, entity);
    }

    private static UserDto MapUser(User user, IReadOnlyList<string> roles) =>
        new(
            user.Id,
            user.OrganizationId,
            user.BranchId,
            user.FullName,
            user.Email,
            user.PhoneNumber,
            user.IsActive,
            user.EmailVerified,
            user.PhoneVerified,
            user.CreatedAt,
            roles);
}
