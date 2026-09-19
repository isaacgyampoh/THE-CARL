using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zazi.Application;
using Zazi.Application.Email;
using Zazi.Application.Security;
using Zazi.Domain;
using Zazi.Infrastructure;
using Zazi.Infrastructure.Security;
using Zazi.Infrastructure.Services;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// Password reset against real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// Two properties carry most of the weight here. The token must be usable exactly once — it
/// sets a password on an account holding financial records, and it sits in a mailbox for as
/// long as it is valid. And nothing the form does may reveal whether an address has an account,
/// because it is a form that must be reachable by people who cannot sign in, which means
/// anybody at all.
/// </para>
/// <para>
/// Real PostgreSQL rather than the in-memory provider, because the atomic claim that makes the
/// token single-use under concurrency is a conditional UPDATE the in-memory provider cannot run.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class PasswordResetTests
{
    private const string OriginalPassword = "Original-Pass-9-Word";
    private const string NewPassword = "Replacement-7-Secret";

    private readonly PostgresFixture _postgres;

    public PasswordResetTests(PostgresFixture postgres) => _postgres = postgres;

    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = new();

        public Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.FromResult(EmailResult.Success());
        }
    }

    /// <summary>Records every log line, so tests can prove what did not reach it.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Sink(Lines);

        public void Dispose()
        {
        }

        private sealed class Sink : ILogger
        {
            private readonly List<string> _lines;

            public Sink(List<string> lines) => _lines = lines;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_lines)
                {
                    _lines.Add(formatter(state, exception));
                }
            }
        }
    }

    private sealed class Harness : IDisposable
    {
        public Harness(PostgresFixture postgres, Action<PasswordResetOptions>? configure = null, string? publicBaseUrl = "https://app.example.com")
        {
            Context = postgres.CreateContext();
            Email = new CapturingEmailSender();
            LogProvider = new CapturingLoggerProvider();

            var options = new PasswordResetOptions
            {
                RequestCooldownMinutes = 0,
                // Zero in tests. The floor is there to hide timing from an attacker, not to slow
                // down a test suite; the one test that cares about it sets its own value.
                MinimumResponseMilliseconds = 0
            };
            configure?.Invoke(options);
            options.Validate();
            Options = options;

            Portal = new PortalOptions { PublicBaseUrl = publicBaseUrl ?? string.Empty };
            Portal.Validate();

            Revocation = new IdentityRevocationService(Context);

            Service = new PasswordResetService(
                Context,
                Email,
                Revocation,
                Microsoft.Extensions.Options.Options.Create(options),
                Microsoft.Extensions.Options.Options.Create(Portal),
                new LoggerFactory(new[] { LogProvider }).CreateLogger<PasswordResetService>());
        }

        public ApplicationDbContext Context { get; }

        public CapturingEmailSender Email { get; }

        public CapturingLoggerProvider LogProvider { get; }

        public PasswordResetOptions Options { get; }

        public PortalOptions Portal { get; }

        public IdentityRevocationService Revocation { get; }

        public PasswordResetService Service { get; }

        public AuthService Auth() => new(
            Context,
            Microsoft.Extensions.Options.Options.Create(new JwtOptions
            {
                Key = "integration-test-signing-key-at-least-32-bytes-long"
            }),
            NullLogger<AuthService>.Instance);

        public void Dispose() => Context.Dispose();
    }

    private static string UniqueEmail() => $"reset-{Guid.NewGuid():N}@example.com";

    /// <summary>Creates an active, password-credential account with a known password.</summary>
    private static async Task<(Guid UserId, string Email)> SeedAccountAsync(
        ApplicationDbContext context,
        bool isActive = true,
        UserCredentialType credentialType = UserCredentialType.Password)
    {
        var email = UniqueEmail();
        var organization = new Organization { Name = $"Reset {Guid.NewGuid():N}" };
        var branch = new Branch { OrganizationId = organization.Id, Name = "Main" };

        var salt = PasswordHashing.NewSalt();
        var user = new User
        {
            OrganizationId = organization.Id,
            BranchId = branch.Id,
            FullName = "Account Holder",
            Email = email,
            IsActive = isActive,
            EmailVerified = true,
            CredentialType = credentialType,
            PasswordSalt = credentialType == UserCredentialType.Password ? salt : string.Empty,
            PasswordHash = credentialType == UserCredentialType.Password
                ? PasswordHashing.Hash(OriginalPassword, salt)
                : string.Empty
        };

        context.Organizations.Add(organization);
        context.Branches.Add(branch);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return (user.Id, email);
    }

    /// <summary>Pulls the token out of the link in a reset email.</summary>
    private static string TokenFrom(EmailMessage message)
    {
        const string marker = "token=";
        var start = message.TextBody.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "The reset email carried no token.");
        start += marker.Length;

        var end = start;
        while (end < message.TextBody.Length && !char.IsWhiteSpace(message.TextBody[end]))
        {
            end++;
        }

        return Uri.UnescapeDataString(message.TextBody[start..end]);
    }

    // ─── Requesting ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AKnownAccountIsSentAResetLink()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (userId, email) = await SeedAccountAsync(harness.Context);

        await harness.Service.RequestResetAsync(email);

        var message = Assert.Single(harness.Email.Sent);
        Assert.Equal(email, message.ToAddress);
        Assert.Contains("https://app.example.com/reset-password?token=", message.TextBody, StringComparison.Ordinal);

        var stored = await harness.Context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.PasswordResetTokenHash, u.PasswordResetExpiresAtUtc })
            .SingleAsync();

        Assert.NotNull(stored.PasswordResetTokenHash);
        Assert.NotNull(stored.PasswordResetExpiresAtUtc);
    }

    [SkippableFact]
    public async Task AnUnknownAccountIsSentNothingAndSaysNothing()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);

        // No exception, no return value, no email. Any of the three would answer the question
        // "does this address have an account?", which is the question this form must not answer.
        await harness.Service.RequestResetAsync(UniqueEmail());

        Assert.Empty(harness.Email.Sent);
    }

    [SkippableFact]
    public async Task BothRequestsAreIndistinguishableToTheCaller()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (_, known) = await SeedAccountAsync(harness.Context);

        // The service returns void and throws for neither, which is the whole public surface:
        // there is nothing for a caller to branch on. The page above it renders one message.
        await harness.Service.RequestResetAsync(known);
        await harness.Service.RequestResetAsync(UniqueEmail());

        // Only the mailbox learns the difference, and only the one that owns the account.
        Assert.Single(harness.Email.Sent);
    }

    [SkippableFact]
    public async Task BothRequestsTakeAtLeastTheConfiguredFloor()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres, options => options.MinimumResponseMilliseconds = 400);
        var (_, known) = await SeedAccountAsync(harness.Context);

        var knownElapsed = System.Diagnostics.Stopwatch.StartNew();
        await harness.Service.RequestResetAsync(known);
        knownElapsed.Stop();

        var unknownElapsed = System.Diagnostics.Stopwatch.StartNew();
        await harness.Service.RequestResetAsync(UniqueEmail());
        unknownElapsed.Stop();

        // Without a floor the unknown path is a single lookup and the known one writes a token
        // and calls an email provider — a difference a stopwatch reads off easily, which would
        // undo the identical response text entirely.
        Assert.True(knownElapsed.ElapsedMilliseconds >= 380, $"known path returned in {knownElapsed.ElapsedMilliseconds}ms");
        Assert.True(unknownElapsed.ElapsedMilliseconds >= 380, $"unknown path returned in {unknownElapsed.ElapsedMilliseconds}ms");
    }

    [SkippableFact]
    public async Task AnInactiveAccountIsNotSentAResetLink()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (_, email) = await SeedAccountAsync(harness.Context, isActive: false);

        await harness.Service.RequestResetAsync(email);

        // An owner who has not verified their address needs the verification link, not a reset.
        // Issuing one here would activate an account through the back door.
        Assert.Empty(harness.Email.Sent);
    }

    [SkippableFact]
    public async Task AnActivationOnlyWorkerIsNotSentAResetLink()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (_, email) = await SeedAccountAsync(
            harness.Context, credentialType: UserCredentialType.ActivationOnly);

        await harness.Service.RequestResetAsync(email);

        // They have no password to reset. Giving them one would hand a credential to an
        // identity the architecture deliberately leaves credential-less.
        Assert.Empty(harness.Email.Sent);
    }

    [SkippableFact]
    public async Task RepeatRequestsAreThrottled()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres, options => options.RequestCooldownMinutes = 60);
        var (_, email) = await SeedAccountAsync(harness.Context);

        await harness.Service.RequestResetAsync(email);
        await harness.Service.RequestResetAsync(email);

        // Otherwise the form is a way to have Zazi send unlimited mail to any address that
        // happens to have an account.
        Assert.Single(harness.Email.Sent);
    }

    [SkippableFact]
    public async Task ANewRequestRetiresThePreviousLink()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (_, email) = await SeedAccountAsync(harness.Context);

        await harness.Service.RequestResetAsync(email);
        var first = TokenFrom(harness.Email.Sent[0]);

        await harness.Service.RequestResetAsync(email);
        var second = TokenFrom(harness.Email.Sent[1]);

        Assert.NotEqual(first, second);

        // People ask again when they think the first link went astray — which is exactly when
        // the first should stop working.
        Assert.Equal(PasswordResetTokenState.InvalidOrAlreadyUsed, await harness.Service.InspectTokenAsync(first));
        Assert.Equal(PasswordResetTokenState.Valid, await harness.Service.InspectTokenAsync(second));
    }

    [SkippableFact]
    public async Task NothingIsSentWhenNoPublicUrlIsConfigured()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres, publicBaseUrl: null);
        var (_, email) = await SeedAccountAsync(harness.Context);

        Assert.False(harness.Service.IsAvailable);

        // A link cannot be built, so none is sent. Reported as a deployment fault in the log
        // rather than as anything the caller can see.
        await harness.Service.RequestResetAsync(email);

        Assert.Empty(harness.Email.Sent);
        Assert.Contains(harness.LogProvider.Lines, line => line.Contains("PublicBaseUrl", StringComparison.Ordinal));
    }

    // ─── The token ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OnlyTheHashOfTheTokenIsStored()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (userId, email) = await SeedAccountAsync(harness.Context);

        await harness.Service.RequestResetAsync(email);
        var token = TokenFrom(Assert.Single(harness.Email.Sent));

        var stored = await harness.Context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.PasswordResetTokenHash)
            .SingleAsync();

        // A copy of this table must not let anyone set passwords on accounts they were never
        // emailed about.
        Assert.NotNull(stored);
        Assert.NotEqual(token, stored);
        Assert.DoesNotContain(token, stored!, StringComparison.Ordinal);
        Assert.Equal(64, stored!.Length); // SHA-256, hex
    }

    [SkippableFact]
    public async Task AValidTokenIsAccepted()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (_, email) = await SeedAccountAsync(harness.Context);

        await harness.Service.RequestResetAsync(email);
        var token = TokenFrom(Assert.Single(harness.Email.Sent));

        Assert.Equal(PasswordResetTokenState.Valid, await harness.Service.InspectTokenAsync(token));
    }

    [SkippableFact]
    public async Task InspectingATokenDoesNotConsumeIt()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (_, email) = await SeedAccountAsync(harness.Context);

        await harness.Service.RequestResetAsync(email);
        var token = TokenFrom(Assert.Single(harness.Email.Sent));

        // Mail clients prefetch links. If loading the page burned the token, the link would be
        // dead before its owner ever clicked it.
        await harness.Service.InspectTokenAsync(token);
        await harness.Service.InspectTokenAsync(token);

        Assert.True((await harness.Service.ResetPasswordAsync(token, NewPassword)).Succeeded);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    public async Task AnInvalidTokenIsRefused(string token)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);

        Assert.Equal(PasswordResetTokenState.InvalidOrAlreadyUsed, await harness.Service.InspectTokenAsync(token));

        var result = await harness.Service.ResetPasswordAsync(token, NewPassword);
        Assert.Equal(PasswordResetOutcome.InvalidOrAlreadyUsed, result.Outcome);
    }

    [SkippableFact]
    public async Task AnExpiredTokenFailsSafely()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (userId, email) = await SeedAccountAsync(harness.Context);

        await harness.Service.RequestResetAsync(email);
        var token = TokenFrom(Assert.Single(harness.Email.Sent));

        var user = await harness.Context.Users.SingleAsync(u => u.Id == userId);
        user.PasswordResetExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await harness.Context.SaveChangesAsync();

        Assert.Equal(PasswordResetTokenState.Expired, await harness.Service.InspectTokenAsync(token));

        var result = await harness.Service.ResetPasswordAsync(token, NewPassword);
        Assert.Equal(PasswordResetOutcome.Expired, result.Outcome);

        // And the password is untouched.
        var auth = harness.Auth();
        var session = await auth.LoginAsync(new LoginRequest(email, OriginalPassword));
        Assert.False(string.IsNullOrWhiteSpace(session.AccessToken));
    }

    [SkippableFact]
    public async Task ATokenCannotBeReusedAfterASuccessfulReset()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (_, email) = await SeedAccountAsync(harness.Context);

        await harness.Service.RequestResetAsync(email);
        var token = TokenFrom(Assert.Single(harness.Email.Sent));

        Assert.True((await harness.Service.ResetPasswordAsync(token, NewPassword)).Succeeded);

        // The link stays in the mailbox forever. Whoever reads that mailbox later must not be
        // able to set the password again.
        var second = await harness.Service.ResetPasswordAsync(token, "Another-Pass-5-Entirely");
        Assert.Equal(PasswordResetOutcome.InvalidOrAlreadyUsed, second.Outcome);

        Assert.Equal(PasswordResetTokenState.InvalidOrAlreadyUsed, await harness.Service.InspectTokenAsync(token));
    }

    [SkippableFact]
    public async Task ConcurrentUseOfOneTokenChangesThePasswordOnce()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var setup = new Harness(_postgres);
        var (_, email) = await SeedAccountAsync(setup.Context);
        await setup.Service.RequestResetAsync(email);
        var token = TokenFrom(Assert.Single(setup.Email.Sent));

        // Separate harnesses, so each attempt gets its own DbContext and its own transaction —
        // which is what two simultaneous requests actually get. One shared context would
        // serialise them through EF and prove nothing about the database.
        using var first = new Harness(_postgres);
        using var second = new Harness(_postgres);

        using var barrier = new Barrier(2);

        Task<PasswordResetResult> Attempt(Harness harness, string password) => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await harness.Service.ResetPasswordAsync(token, password);
        });

        var results = await Task.WhenAll(
            Attempt(first, NewPassword),
            Attempt(second, "Different-Pass-4-Here"));

        // Exactly one wins. The checks before the claim are advisory — both callers can pass
        // them — so the conditional UPDATE is what decides, and the loser is refused rather
        // than quietly writing a second password over the first.
        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(1, results.Count(r => r.Outcome == PasswordResetOutcome.InvalidOrAlreadyUsed));
    }

    // ─── Changing the password ───────────────────────────────────────────────

    [SkippableFact]
    public async Task TheNewPasswordWorksThroughTheRealAuthServiceAndTheOldOneDoesNot()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (_, email) = await SeedAccountAsync(harness.Context);

        await harness.Service.RequestResetAsync(email);
        var token = TokenFrom(Assert.Single(harness.Email.Sent));

        Assert.True((await harness.Service.ResetPasswordAsync(token, NewPassword)).Succeeded);

        var auth = harness.Auth();

        // Through AuthService, not by re-reading the column. Two classes hash passwords here,
        // and if their PBKDF2 parameters ever diverged this is the test that would notice.
        var session = await auth.LoginAsync(new LoginRequest(email, NewPassword));
        Assert.False(string.IsNullOrWhiteSpace(session.AccessToken));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => auth.LoginAsync(new LoginRequest(email, OriginalPassword)));
    }

    [SkippableTheory]
    [InlineData("short")]
    [InlineData("alllowercaseletters")]
    [InlineData("")]
    public async Task AWeakNewPasswordIsRefusedWithoutSpendingTheToken(string weak)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (_, email) = await SeedAccountAsync(harness.Context);

        await harness.Service.RequestResetAsync(email);
        var token = TokenFrom(Assert.Single(harness.Email.Sent));

        var rejected = await harness.Service.ResetPasswordAsync(token, weak);

        Assert.Equal(PasswordResetOutcome.PasswordRejected, rejected.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(rejected.Problem));

        // The token survives. Choosing a weak password is not a reason to make someone request
        // a whole new link.
        Assert.Equal(PasswordResetTokenState.Valid, await harness.Service.InspectTokenAsync(token));
        Assert.True((await harness.Service.ResetPasswordAsync(token, NewPassword)).Succeeded);
    }

    [SkippableFact]
    public async Task TheSamePolicyAppliesHereAsEverywhereElse()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (_, email) = await SeedAccountAsync(harness.Context);

        await harness.Service.RequestResetAsync(email);
        var token = TokenFrom(Assert.Single(harness.Email.Sent));

        // Asserted against PasswordPolicy itself rather than a restated rule, so the two cannot
        // drift into disagreeing about what an acceptable password is.
        const string weak = "nocapsordigits";
        Assert.NotNull(PasswordPolicy.Describe(weak));

        var result = await harness.Service.ResetPasswordAsync(token, weak);
        Assert.Equal(PasswordResetOutcome.PasswordRejected, result.Outcome);
        Assert.Equal(PasswordPolicy.Describe(weak), result.Problem);
    }

    // ─── Sessions afterwards ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task ResettingRotatesTheSecurityStampAndClearsLockout()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (userId, email) = await SeedAccountAsync(harness.Context);

        var before = await harness.Context.Users.AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync();

        // Lock the account the way repeated failures would.
        var locked = await harness.Context.Users.SingleAsync(u => u.Id == userId);
        locked.FailedLoginAttempts = 5;
        locked.LockoutUntilUtc = DateTimeOffset.UtcNow.AddHours(1);
        await harness.Context.SaveChangesAsync();

        await harness.Service.RequestResetAsync(email);
        var token = TokenFrom(Assert.Single(harness.Email.Sent));
        Assert.True((await harness.Service.ResetPasswordAsync(token, NewPassword)).Succeeded);

        var after = await harness.Context.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.SecurityStamp, u.FailedLoginAttempts, u.LockoutUntilUtc })
            .SingleAsync();

        // The stamp is what invalidates browser cookies and refuses the next token refresh. A
        // reset that left whoever forced it still signed in would defeat the point.
        Assert.NotEqual(before, after.SecurityStamp);

        // And someone who was locked out but has now proved control of the mailbox has answered
        // the question lockout was asking.
        Assert.Equal(0, after.FailedLoginAttempts);
        Assert.Null(after.LockoutUntilUtc);

        var session = await harness.Auth().LoginAsync(new LoginRequest(email, NewPassword));
        Assert.False(string.IsNullOrWhiteSpace(session.AccessToken));
    }

    // ─── Isolation ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ResettingOneAccountLeavesEveryOtherAccountAlone()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (targetId, target) = await SeedAccountAsync(harness.Context);
        var (bystanderId, bystander) = await SeedAccountAsync(harness.Context);

        var bystanderBefore = await harness.Context.Users.AsNoTracking()
            .Where(u => u.Id == bystanderId)
            .Select(u => new { u.PasswordHash, u.PasswordSalt, u.SecurityStamp })
            .SingleAsync();

        await harness.Service.RequestResetAsync(target);
        var token = TokenFrom(Assert.Single(harness.Email.Sent));
        Assert.True((await harness.Service.ResetPasswordAsync(token, NewPassword)).Succeeded);

        var bystanderAfter = await harness.Context.Users.AsNoTracking()
            .Where(u => u.Id == bystanderId)
            .Select(u => new { u.PasswordHash, u.PasswordSalt, u.SecurityStamp })
            .SingleAsync();

        Assert.Equal(bystanderBefore.PasswordHash, bystanderAfter.PasswordHash);
        Assert.Equal(bystanderBefore.PasswordSalt, bystanderAfter.PasswordSalt);
        Assert.Equal(bystanderBefore.SecurityStamp, bystanderAfter.SecurityStamp);

        // The other account's own password still works, and the reset one's does not.
        var auth = harness.Auth();
        Assert.False(string.IsNullOrWhiteSpace(
            (await auth.LoginAsync(new LoginRequest(bystander, OriginalPassword))).AccessToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => auth.LoginAsync(new LoginRequest(target, OriginalPassword)));

        Assert.NotEqual(targetId, bystanderId);
    }

    [SkippableFact]
    public async Task ATokenOnlyEverResetsTheAccountItWasIssuedFor()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (firstId, first) = await SeedAccountAsync(harness.Context);
        var (secondId, second) = await SeedAccountAsync(harness.Context);

        await harness.Service.RequestResetAsync(first);
        await harness.Service.RequestResetAsync(second);

        var firstToken = TokenFrom(harness.Email.Sent[0]);
        Assert.True((await harness.Service.ResetPasswordAsync(firstToken, NewPassword)).Succeeded);

        // The second account's token is untouched by the first account's reset.
        var secondPending = await harness.Context.Users.AsNoTracking()
            .Where(u => u.Id == secondId).Select(u => u.PasswordResetTokenHash).SingleAsync();
        Assert.NotNull(secondPending);

        var firstPending = await harness.Context.Users.AsNoTracking()
            .Where(u => u.Id == firstId).Select(u => u.PasswordResetTokenHash).SingleAsync();
        Assert.Null(firstPending);
    }

    // ─── What must not appear anywhere ───────────────────────────────────────

    [SkippableFact]
    public async Task NeitherTheTokenNorThePasswordReachesTheAuditLogOrTheLogger()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (userId, email) = await SeedAccountAsync(harness.Context);

        await harness.Service.RequestResetAsync(email);
        var message = Assert.Single(harness.Email.Sent);
        var token = TokenFrom(message);

        Assert.True((await harness.Service.ResetPasswordAsync(token, NewPassword)).Succeeded);

        var entries = await harness.Context.AuditLogs
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .ToListAsync();

        Assert.Contains(entries, a => a.Action == "PASSWORD_RESET_REQUESTED");
        Assert.Contains(entries, a => a.Action == "PASSWORD_RESET_COMPLETED");

        var tokenHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

        foreach (var detail in entries.Select(a => a.Details ?? string.Empty))
        {
            Assert.DoesNotContain(token, detail, StringComparison.Ordinal);
            Assert.DoesNotContain(tokenHash, detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(NewPassword, detail, StringComparison.Ordinal);
            Assert.DoesNotContain(OriginalPassword, detail, StringComparison.Ordinal);
        }

        foreach (var line in harness.LogProvider.Lines)
        {
            Assert.DoesNotContain(token, line, StringComparison.Ordinal);
            Assert.DoesNotContain(tokenHash, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(NewPassword, line, StringComparison.Ordinal);
            // The full address is masked too, so a log file is not a mailing list.
            Assert.DoesNotContain(email, line, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task TheResetEmailSaysNothingAboutTheAccountBeyondItsExistence()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (_, email) = await SeedAccountAsync(harness.Context);

        await harness.Service.RequestResetAsync(email);
        var message = Assert.Single(harness.Email.Sent);

        // Whoever asked for the reset has not proved they are the account holder yet. Until the
        // link is opened, the only thing they are known to control is the mailbox — so the
        // message names no person, business or branch.
        Assert.DoesNotContain("Account Holder", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Account Holder", message.HtmlBody, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AConfirmationIsSentAfterTheChange()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var (_, email) = await SeedAccountAsync(harness.Context);

        await harness.Service.RequestResetAsync(email);
        var token = TokenFrom(harness.Email.Sent[0]);
        await harness.Service.ResetPasswordAsync(token, NewPassword);

        Assert.Equal(2, harness.Email.Sent.Count);
        var confirmation = harness.Email.Sent[1];

        Assert.Contains("changed", confirmation.Subject, StringComparison.OrdinalIgnoreCase);

        // No link to click. A message announcing a security event is exactly the shape a
        // phishing message wants to take, so this one gives nothing to click.
        Assert.DoesNotContain("reset-password?token=", confirmation.TextBody, StringComparison.Ordinal);
    }
}
