using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zazi.Application.Email;
using Zazi.Application.Onboarding;
using Zazi.Application.Security;
using Zazi.Domain;
using Zazi.Infrastructure.Security;

namespace Zazi.Infrastructure.Onboarding;

/// <summary>
/// Self-service onboarding. See <see cref="ISignUpService"/> for the rules this enforces.
/// </summary>
public sealed class SignUpService : ISignUpService
{
    /// <summary>
    /// Bytes of entropy in a verification token.
    /// </summary>
    /// <remarks>
    /// 32, because this token activates an account on its own. It is not a six-digit code
    /// somebody types — it arrives in a link — so there is no usability argument for making it
    /// short, and no reason to leave guessing on the table.
    /// </remarks>
    private const int TokenBytes = 32;

    private readonly ApplicationDbContext _dbContext;
    private readonly IEmailSender _email;
    private readonly SignUpOptions _options;
    private readonly PortalOptions _portal;
    private readonly ILogger<SignUpService> _logger;

    public SignUpService(
        ApplicationDbContext dbContext,
        IEmailSender email,
        IOptions<SignUpOptions> options,
        IOptions<PortalOptions> portal,
        ILogger<SignUpService> logger)
    {
        _dbContext = dbContext;
        _email = email;
        _options = options.Value;
        _portal = portal.Value;
        _logger = logger;
    }

    public async Task<SignUpResult> SignUpAsync(
        SignUpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Self-service signup is not enabled on this deployment.");
        }

        var businessName = (request.BusinessName ?? string.Empty).Trim();
        var fullName = (request.FullName ?? string.Empty).Trim();
        var email = NormalizeEmail(request.Email ?? string.Empty);

        if (string.IsNullOrWhiteSpace(businessName))
        {
            throw new ArgumentException("Business name is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new ArgumentException("Your name is required.", nameof(request));
        }

        if (!LooksLikeEmailAddress(email))
        {
            throw new ArgumentException("A valid email address is required.", nameof(request));
        }

        PasswordPolicy.Validate(request.Password, nameof(request));

        var (token, tokenHash) = NewToken();
        var now = DateTimeOffset.UtcNow;

        var organization = new Organization
        {
            Name = businessName,
            Email = email,
            PhoneNumber = request.PhoneNumber
        };

        var branch = new Branch
        {
            OrganizationId = organization.Id,
            Name = _options.FirstBranchName
        };

        var owner = new User
        {
            OrganizationId = organization.Id,
            // Organization-wide, so no branch. An owner scoped to one branch could not see the
            // business they just created.
            BranchId = null,
            FullName = fullName,
            Email = email,
            PhoneNumber = request.PhoneNumber,
            CredentialType = UserCredentialType.Password,

            // The rule this whole flow exists to enforce. Login refuses an inactive user, so
            // until the link in the email is opened there is no account anyone can sign into —
            // including whoever typed the address, if it was not theirs to type.
            IsActive = false,
            EmailVerified = false,

            EmailVerificationTokenHash = tokenHash,
            EmailVerificationExpiresAtUtc = now.AddHours(_options.VerificationValidForHours),
            EmailVerificationSentAtUtc = now
        };

        SetPassword(owner, request.Password);
        owner.Roles.Add(BuildOwnerRole(organization.Id));

        // One transaction. An organization with no owner, or an owner with no branch, is not a
        // state any later screen knows how to repair, and nothing would report it.
        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        // Serialise every concurrent signup for this one address, for the life of the
        // transaction.
        //
        // Not premature. The duplicate check below and the insert that follows it are separate
        // statements, so without this two requests can both find nothing and both create an
        // organization — and the way that happens in practice is not an attack, it is somebody
        // double-clicking the submit button. The database cannot catch it either: Email is
        // unique per organization, and these would be two different organizations.
        //
        // An advisory lock rather than a table lock: it blocks only other signups for the same
        // address, and PostgreSQL releases it at commit or rollback whatever happens.
        if (_dbContext.Database.IsRelational())
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({email}))",
                cancellationToken);
        }

        // Any existing account on this address, in any organization. Login enumerates every
        // user with a matching address and tries the password against each, so a second tenant
        // sharing an address is not a conflict the database rejects — it is an ambiguity at
        // sign-in, resolved by whichever password happens to match. Not something to create.
        var addressAlreadyKnown = await _dbContext.Users
            .AnyAsync(u => u.Email == email, cancellationToken);

        if (addressAlreadyKnown)
        {
            if (transaction is not null)
            {
                // Nothing was written, but the lock is held until the transaction ends.
                await transaction.RollbackAsync(cancellationToken);
            }

            // No account is created and the caller is not told. The person who owns the address
            // is told, by email, because they are the only one who should learn this.
            await SendAsync(
                OnboardingEmails.AlreadyRegistered(email, _portal.SignInUrl()),
                cancellationToken);

            _logger.LogInformation(
                "Signup attempted for an address that already has an account: {Recipient}.",
                EmailLogSafety.MaskRecipient(email));

            return new SignUpResult(SignUpOutcome.VerificationSent);
        }

        _dbContext.Organizations.Add(organization);
        _dbContext.Branches.Add(branch);
        _dbContext.Users.Add(owner);
        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = organization.Id,
            UserId = owner.Id,
            Action = "ORGANIZATION_SIGNED_UP",
            Details = "Organization created by self-service signup. Owner is inactive pending email verification.",
            ActorType = "Self"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        // After the commit, deliberately. An email promising a link to an account that was
        // rolled back is worse than a delayed email, and the send is the slow part.
        var sent = await SendAsync(
            OnboardingEmails.Verification(email, businessName, _portal.VerifyEmailUrl(token)),
            cancellationToken);

        return new SignUpResult(
            sent ? SignUpOutcome.VerificationSent : SignUpOutcome.CreatedButEmailFailed);
    }

    public async Task<EmailVerificationResult> VerifyEmailAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new EmailVerificationResult(EmailVerificationOutcome.InvalidOrAlreadyUsed);
        }

        var tokenHash = HashToken(token);

        // By hash. The token is never stored, so a copy of this table does not let anyone
        // activate an account they have not been emailed about.
        var user = await _dbContext.Users
            .SingleOrDefaultAsync(u => u.EmailVerificationTokenHash == tokenHash, cancellationToken);

        if (user is null)
        {
            // Never used, already used, or the account is gone. One answer for all three: the
            // differences are only informative to someone testing tokens they were not sent.
            return new EmailVerificationResult(EmailVerificationOutcome.InvalidOrAlreadyUsed);
        }

        if (user.EmailVerificationExpiresAtUtc is not { } expiry || expiry <= DateTimeOffset.UtcNow)
        {
            // Left in place, so a fresh link can be requested. Clearing it here would make an
            // expired link indistinguishable from a used one and lose the account its route back.
            return new EmailVerificationResult(EmailVerificationOutcome.Expired);
        }

        user.EmailVerified = true;
        user.IsActive = true;

        // Cleared, which is what makes the token single-use. A verification link forwarded in a
        // mailbox someone else later reads must not still work.
        user.EmailVerificationTokenHash = null;
        user.EmailVerificationExpiresAtUtc = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = user.OrganizationId,
            UserId = user.Id,
            Action = "EMAIL_VERIFIED",
            Details = "Owner verified their email address and the account was activated.",
            ActorType = "Self"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new EmailVerificationResult(EmailVerificationOutcome.Verified);
    }

    public async Task ResendVerificationAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeEmail(email ?? string.Empty);
        if (!LooksLikeEmailAddress(normalized))
        {
            return;
        }

        var user = await _dbContext.Users
            .Where(u => u.Email == normalized && !u.EmailVerified && !u.IsActive)
            .OrderByDescending(u => u.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        // No match, so nothing happens and nothing is said. Reporting it would make this the
        // enumeration oracle the signup form is careful not to be.
        if (user is null)
        {
            return;
        }

        if (user.EmailVerificationSentAtUtc is { } lastSent
            && DateTimeOffset.UtcNow - lastSent < TimeSpan.FromMinutes(_options.ResendCooldownMinutes))
        {
            // Silently. Telling the caller they are too early turns this into a way to measure
            // whether the address exists, which is precisely what the check above avoided.
            _logger.LogInformation(
                "Verification resend throttled for {Recipient}.",
                EmailLogSafety.MaskRecipient(normalized));
            return;
        }

        var organizationName = await _dbContext.Organizations
            .Where(o => o.Id == user.OrganizationId)
            .Select(o => o.Name)
            .SingleOrDefaultAsync(cancellationToken) ?? "your business";

        var (token, tokenHash) = NewToken();
        var now = DateTimeOffset.UtcNow;

        // A new token, not the old one resent. The previous link stops working the moment this
        // one is issued, so a link in an older email cannot be used after the person has asked
        // for a replacement — which is usually what they do when they suspect the first went astray.
        user.EmailVerificationTokenHash = tokenHash;
        user.EmailVerificationExpiresAtUtc = now.AddHours(_options.VerificationValidForHours);
        user.EmailVerificationSentAtUtc = now;
        user.UpdatedAt = now;

        await _dbContext.SaveChangesAsync(cancellationToken);

        await SendAsync(
            OnboardingEmails.Verification(normalized, organizationName, _portal.VerifyEmailUrl(token)),
            cancellationToken);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var result = await _email.SendAsync(message, cancellationToken);

        if (!result.Sent)
        {
            // Logged rather than thrown. The account exists and is recoverable by resending;
            // throwing here would turn a delivery problem into a failed signup and lose the
            // organization that was just committed.
            _logger.LogError(
                "Onboarding email to {Recipient} was not sent: {Reason}",
                EmailLogSafety.MaskRecipient(message.ToAddress),
                result.FailureReason);
        }

        return result.Sent;
    }

    /// <summary>A token to email, and the hash to store.</summary>
    private static (string Token, string Hash) NewToken()
    {
        // Base64url: it travels in a query string without escaping, so the link in the email is
        // the link that arrives, however a mail client decides to rewrite it.
        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));
        return (token, HashToken(token));
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>
    /// A deliberately shallow check.
    /// </summary>
    /// <remarks>
    /// Validating email addresses properly is not possible with a pattern, and trying produces
    /// rules that reject real addresses. Whether the address works is settled by whether the
    /// verification email arrives, which this flow is built around anyway.
    /// </remarks>
    private static bool LooksLikeEmailAddress(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at > 0
            && at < email.Length - 1
            && email.IndexOf('@', at + 1) < 0
            && email.Contains('.', StringComparison.Ordinal)
            && !email.Any(char.IsWhiteSpace);
    }

    private static void SetPassword(User user, string password)
    {
        // The same PBKDF2 parameters AuthService verifies against. They are stated here because
        // the hashing helpers are private to that class; PasswordHashing is the shared copy so
        // the two cannot drift.
        var salt = PasswordHashing.NewSalt();
        user.PasswordSalt = salt;
        user.PasswordHash = PasswordHashing.Hash(password, salt);
    }

    private static Role BuildOwnerRole(Guid organizationId)
    {
        var role = new Role
        {
            OrganizationId = organizationId,
            Name = ZaziRoles.Owner,
            Description = "Owner role for the organization."
        };

        foreach (var permission in ZaziPolicies.RolesByPolicy
                     .Where(entry => entry.Value.Contains(ZaziRoles.Owner, StringComparer.Ordinal))
                     .Select(entry => entry.Key)
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            role.Permissions.Add(new Permission
            {
                RoleId = role.Id,
                Name = permission,
                Description = $"Allows {permission} scoped to the tenant."
            });
        }

        return role;
    }
}
