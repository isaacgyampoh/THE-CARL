using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zazi.Application.Email;
using Zazi.Application.Security;
using Zazi.Domain;

namespace Zazi.Infrastructure.Security;

/// <summary>
/// Resetting a forgotten password. See <see cref="IPasswordResetService"/> for the rules.
/// </summary>
/// <remarks>
/// Assembled from pieces that already existed: <see cref="PasswordPolicy"/>,
/// <see cref="PasswordHashing"/>, <c>IEmailSender</c>, <see cref="IIdentityRevocationService"/>
/// and the hashed single-use token pattern email verification introduced. Nothing about
/// authentication is re-decided here.
/// </remarks>
public sealed class PasswordResetService : IPasswordResetService
{
    /// <summary>
    /// Bytes of entropy in a reset token.
    /// </summary>
    /// <remarks>
    /// 32, matching email verification. This token is strictly more dangerous — it sets a
    /// password rather than confirming an address — and it arrives in a link, so there is no
    /// usability reason to make it shorter and no reason to leave guessing on the table.
    /// </remarks>
    private const int TokenBytes = 32;

    private readonly ApplicationDbContext _dbContext;
    private readonly IEmailSender _email;
    private readonly IIdentityRevocationService _revocation;
    private readonly PasswordResetOptions _options;
    private readonly PortalOptions _portal;
    private readonly ILogger<PasswordResetService> _logger;

    public PasswordResetService(
        ApplicationDbContext dbContext,
        IEmailSender email,
        IIdentityRevocationService revocation,
        IOptions<PasswordResetOptions> options,
        IOptions<PortalOptions> portal,
        ILogger<PasswordResetService> logger)
    {
        _dbContext = dbContext;
        _email = email;
        _revocation = revocation;
        _options = options.Value;
        _portal = portal.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsAvailable => _portal.IsConfigured;

    // ─── Requesting a link ───────────────────────────────────────────────────

    public async Task RequestResetAsync(string email, CancellationToken cancellationToken = default)
    {
        // Started before anything else, including the validation below. The floor has to cover
        // every path out of this method or the fast ones are the ones that leak.
        var started = Stopwatch.StartNew();

        try
        {
            await IssueResetAsync(email ?? string.Empty, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Swallowed on purpose. This method returns nothing and must behave identically
            // whatever it finds, and an exception escaping would be a signal in itself — a 500
            // for known addresses and a 200 for unknown ones says exactly what the generic
            // response exists to hide.
            _logger.LogError(exception, "Password reset request failed.");
        }
        finally
        {
            await ApplyResponseFloorAsync(started, cancellationToken);
        }
    }

    private async Task IssueResetAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = NormalizeEmail(email);

        if (!LooksLikeEmailAddress(normalized))
        {
            return;
        }

        if (!IsAvailable)
        {
            // Nothing can be sent without a public URL to build the link from. Logged as an
            // error because it is a deployment fault, not a caller fault.
            _logger.LogError(
                "Password reset requested but {Section}:PublicBaseUrl is not configured, so no "
                + "link can be built.",
                PortalOptions.SectionName);
            return;
        }

        // Every account on this address that could actually use a reset link. Plural because an
        // address may legitimately hold accounts in more than one organization — login already
        // enumerates the same way — and resetting only one of them would silently leave the
        // person locked out of the other with no indication why.
        //
        // Inactive accounts are excluded: an owner who has not verified their address yet needs
        // the verification link, not a password reset, and issuing one would activate an
        // account by the back door. Activation-only workers are excluded because they have no
        // password to reset.
        var candidates = await _dbContext.Users
            .Where(u => u.Email == normalized
                && u.IsActive
                && u.CredentialType == UserCredentialType.Password)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            // Nothing sent, nothing said, nothing logged that names the address as unknown.
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromMinutes(_options.RequestCooldownMinutes);
        var issued = new List<(User User, string Token)>();

        foreach (var user in candidates)
        {
            if (user.PasswordResetRequestedAtUtc is { } lastRequested
                && now - lastRequested < cooldown)
            {
                // Silently. Telling the caller they are too early would reveal that the address
                // exists, which is the one thing this method must never do.
                continue;
            }

            var (token, tokenHash) = NewToken();

            // A new token retires any previous one, because the column holds exactly one. That
            // is the behaviour worth having: people ask again when they suspect the first link
            // went astray, and the old link should stop working at that moment.
            user.PasswordResetTokenHash = tokenHash;
            user.PasswordResetExpiresAtUtc = now.AddMinutes(_options.ValidForMinutes);
            user.PasswordResetRequestedAtUtc = now;
            user.UpdatedAt = now;

            _dbContext.AuditLogs.Add(new AuditLogEntry
            {
                OrganizationId = user.OrganizationId,
                UserId = user.Id,
                Action = "PASSWORD_RESET_REQUESTED",
                // No token, no hash, no address. The user and organization identify the account
                // for anyone reading the log, and none of the rest would help them.
                Details = $"A password reset link was issued and is valid for {_options.ValidForMinutes} minutes.",
                ActorType = "Self"
            });

            issued.Add((user, token));
        }

        if (issued.Count == 0)
        {
            return;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // After the commit. A link that works must not be emailed before the row that makes it
        // work is durable.
        foreach (var (user, token) in issued)
        {
            var message = AccountEmails.PasswordReset(
                user.Email!,
                _portal.ResetPasswordUrl(token),
                _options.ValidForMinutes);

            var result = await _email.SendAsync(message, cancellationToken);

            if (!result.Sent)
            {
                _logger.LogError(
                    "Password reset email to {Recipient} was not sent: {Reason}",
                    EmailLogSafety.MaskRecipient(user.Email!),
                    result.FailureReason);
            }
        }
    }

    // ─── Inspecting a link ───────────────────────────────────────────────────

    public async Task<PasswordResetTokenState> InspectTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return PasswordResetTokenState.InvalidOrAlreadyUsed;
        }

        var hash = HashToken(token);

        var match = await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.PasswordResetTokenHash == hash)
            .Select(u => new { u.PasswordResetExpiresAtUtc, u.IsActive })
            .SingleOrDefaultAsync(cancellationToken);

        if (match is null || !match.IsActive)
        {
            return PasswordResetTokenState.InvalidOrAlreadyUsed;
        }

        return match.PasswordResetExpiresAtUtc is { } expiry && expiry > DateTimeOffset.UtcNow
            ? PasswordResetTokenState.Valid
            : PasswordResetTokenState.Expired;
    }

    // ─── Completing the reset ────────────────────────────────────────────────

    public async Task<PasswordResetResult> ResetPasswordAsync(
        string token,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        // Before the token is touched. A password the policy would refuse must not cost the
        // person their only link — they would have to go back and request another, having done
        // nothing wrong except pick a weak password.
        if (PasswordPolicy.Describe(newPassword) is { } problem)
        {
            return new PasswordResetResult(PasswordResetOutcome.PasswordRejected, problem);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return new PasswordResetResult(PasswordResetOutcome.InvalidOrAlreadyUsed);
        }

        var hash = HashToken(token);

        var pending = await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.PasswordResetTokenHash == hash)
            .Select(u => new { u.Id, u.PasswordResetExpiresAtUtc, u.IsActive })
            .SingleOrDefaultAsync(cancellationToken);

        if (pending is null || !pending.IsActive)
        {
            return new PasswordResetResult(PasswordResetOutcome.InvalidOrAlreadyUsed);
        }

        if (pending.PasswordResetExpiresAtUtc is not { } expiry || expiry <= DateTimeOffset.UtcNow)
        {
            // Left in place rather than cleared, so the person can tell "too late" from
            // "wrong link" and knows to request another.
            return new PasswordResetResult(PasswordResetOutcome.Expired);
        }

        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        // The check above is advisory: two requests carrying the same token can both pass it
        // before either commits. This conditional UPDATE is the boundary — the token is cleared
        // and claimed in one statement, and only one caller can be the one that changed a row.
        // The loser is refused rather than quietly setting a second password over the first.
        if (_dbContext.Database.IsRelational())
        {
            var claimed = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE "Users"
                 SET "PasswordResetTokenHash" = NULL, "PasswordResetExpiresAtUtc" = NULL
                 WHERE "Id" = {pending.Id} AND "PasswordResetTokenHash" = {hash}
                 """,
                cancellationToken);

            if (claimed == 0)
            {
                return new PasswordResetResult(PasswordResetOutcome.InvalidOrAlreadyUsed);
            }
        }

        var user = await _dbContext.Users.SingleAsync(u => u.Id == pending.Id, cancellationToken);

        if (!_dbContext.Database.IsRelational())
        {
            // The in-memory provider cannot run the statement above. Clearing the fields here
            // keeps single-use semantics for tests that do not need a real database; the
            // atomicity it cannot provide is exactly why the concurrency test requires one.
            user.PasswordResetTokenHash = null;
            user.PasswordResetExpiresAtUtc = null;
        }

        // The same hashing the rest of the system verifies against. Not a second copy —
        // AuthService delegates to this too, and a divergence would mean the new password is
        // accepted here and rejected at sign-in.
        var salt = PasswordHashing.NewSalt();
        user.PasswordSalt = salt;
        user.PasswordHash = PasswordHashing.Hash(newPassword, salt);

        // A successful reset clears the lockout. Someone locked out by failed attempts and
        // then proving control of the mailbox has answered the question lockout was asking.
        user.FailedLoginAttempts = 0;
        user.LockoutUntilUtc = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = user.OrganizationId,
            UserId = user.Id,
            Action = "PASSWORD_RESET_COMPLETED",
            // Neither the password nor the token, in any form.
            Details = "Password changed via a reset link. All sessions revoked.",
            ActorType = "Self"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        // The existing mechanism, chosen over anything new: it rotates the security stamp —
        // which is what invalidates browser cookies and refuses the next token refresh — and
        // closes the AuthSessions and refresh-token families as well. A password reset that
        // left whoever forced it still signed in would defeat the point of resetting.
        await _revocation.RevokeAllSessionsAsync(
            user.Id,
            RevocationTrigger.PasswordChanged,
            actorUserId: null,
            cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        // After the commit, and its failure does not fail the reset: the password has already
        // changed, and reporting failure now would tell the person to try again when there is
        // nothing left to try.
        var confirmation = await _email.SendAsync(
            AccountEmails.PasswordChanged(user.Email!, _portal.SignInUrl()),
            cancellationToken);

        if (!confirmation.Sent)
        {
            _logger.LogWarning(
                "Password was reset for {Recipient} but the confirmation email was not sent: {Reason}",
                EmailLogSafety.MaskRecipient(user.Email!),
                confirmation.FailureReason);
        }

        return new PasswordResetResult(PasswordResetOutcome.Changed);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Holds the response until the configured floor has elapsed.
    /// </summary>
    /// <remarks>
    /// The work done for a known address — writing a token, calling the email provider — takes
    /// far longer than the single lookup an unknown one costs. Returning as soon as the work is
    /// done would let anyone distinguish the two by stopwatch, which is precisely what the
    /// identical response text exists to prevent.
    /// </remarks>
    private async Task ApplyResponseFloorAsync(Stopwatch started, CancellationToken cancellationToken)
    {
        var remaining = _options.MinimumResponseMilliseconds - (int)started.ElapsedMilliseconds;
        if (remaining > 0)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }

    private static (string Token, string Hash) NewToken()
    {
        // Base64url, so the token survives a query string unchanged however a mail client
        // decides to rewrite the link.
        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));
        return (token, HashToken(token));
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static bool LooksLikeEmailAddress(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at > 0
            && at < email.Length - 1
            && email.IndexOf('@', at + 1) < 0
            && email.Contains('.', StringComparison.Ordinal)
            && !email.Any(char.IsWhiteSpace);
    }
}
