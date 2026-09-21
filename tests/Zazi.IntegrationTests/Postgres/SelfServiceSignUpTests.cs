using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zazi.Application;
using Zazi.Application.Email;
using Zazi.Application.Onboarding;
using Zazi.Application.Security;
using Zazi.Domain;
using Zazi.Infrastructure;
using Zazi.Infrastructure.Onboarding;
using Zazi.Infrastructure.Services;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// Self-service onboarding against real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// This is the only path that creates a tenant with no authenticated caller behind it, which
/// makes every refusal in it load-bearing. The tests that matter most are the ones about the
/// window between signing up and verifying: an account that works before anyone has proved
/// they own the address is an account that can be opened in someone else's name.
/// </para>
/// <para>
/// Against real PostgreSQL rather than the in-memory provider, because the transaction, the
/// filtered index and the uniqueness behaviour this relies on are all things the in-memory
/// provider pretends to have.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class SelfServiceSignUpTests
{
    private const string GoodPassword = "Correct-Horse-9-Battery";

    private readonly PostgresFixture _postgres;

    public SelfServiceSignUpTests(PostgresFixture postgres) => _postgres = postgres;

    /// <summary>Captures what would have been sent, so tests can read the link.</summary>
    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = new();

        public bool FailEverything { get; set; }

        public Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            if (FailEverything)
            {
                return Task.FromResult(EmailResult.TransientFailure("test: delivery disabled"));
            }

            Sent.Add(message);
            return Task.FromResult(EmailResult.Success());
        }
    }

    private sealed class Harness : IDisposable
    {
        public Harness(
            PostgresFixture postgres,
            Action<SignUpOptions>? configure = null,
            Action<PortalOptions>? configurePortal = null)
        {
            Context = postgres.CreateContext();
            Email = new CapturingEmailSender();

            var options = new SignUpOptions
            {
                Enabled = true,
                ResendCooldownMinutes = 0
            };
            var portal = new PortalOptions { PublicBaseUrl = "https://app.example.com" };
            configure?.Invoke(options);
            configurePortal?.Invoke(portal);
            portal.Validate();
            options.Validate(portal);
            Options = options;
            Portal = portal;

            Service = new SignUpService(
                Context,
                Email,
                Microsoft.Extensions.Options.Options.Create(options),
                Microsoft.Extensions.Options.Options.Create(portal),
                NullLogger<SignUpService>.Instance);
        }

        public ApplicationDbContext Context { get; }

        public CapturingEmailSender Email { get; }

        public SignUpOptions Options { get; }

        public PortalOptions Portal { get; }

        public SignUpService Service { get; }

        public void Dispose() => Context.Dispose();
    }

    private static string UniqueEmail() => $"owner-{Guid.NewGuid():N}@example.com";

    /// <summary>Pulls the token out of the link in the verification email.</summary>
    private static string TokenFrom(EmailMessage message)
    {
        var marker = "token=";
        var start = message.TextBody.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "The verification email carried no token.");
        start += marker.Length;

        var end = start;
        while (end < message.TextBody.Length && !char.IsWhiteSpace(message.TextBody[end]))
        {
            end++;
        }

        return Uri.UnescapeDataString(message.TextBody[start..end]);
    }

    private static SignUpRequest Request(string email) =>
        new("Kofi Mobile Money", "Kofi Asante", email, GoodPassword, "+233201234567");

    // ─── Creating the tenant ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task SigningUpCreatesAnOrganizationABranchAndAnOwner()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var email = UniqueEmail();

        await harness.Service.SignUpAsync(Request(email));

        var owner = await harness.Context.Users
            .Include(u => u.Roles)
            .SingleAsync(u => u.Email == email);

        var organization = await harness.Context.Organizations.SingleAsync(o => o.Id == owner.OrganizationId);
        var branches = await harness.Context.Branches.Where(b => b.OrganizationId == organization.Id).ToListAsync();

        Assert.Equal("Kofi Mobile Money", organization.Name);
        Assert.Equal("Kofi Asante", owner.FullName);
        Assert.Single(branches);

        // Organization-wide, so no branch. An owner scoped to one branch could not see the
        // business they just created.
        Assert.Null(owner.BranchId);
        Assert.Contains(owner.Roles, r => r.Name == ZaziRoles.Owner);
    }

    [SkippableFact]
    public async Task TheOwnerIsInactiveAndUnverifiedUntilTheLinkIsOpened()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var email = UniqueEmail();

        await harness.Service.SignUpAsync(Request(email));

        var owner = await harness.Context.Users.SingleAsync(u => u.Email == email);

        // The rule the whole flow exists to enforce.
        Assert.False(owner.IsActive);
        Assert.False(owner.EmailVerified);
    }

    [SkippableFact]
    public async Task AnUnverifiedOwnerCannotSignIn()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var email = UniqueEmail();

        await harness.Service.SignUpAsync(Request(email));

        // Through the real AuthService, not by re-reading the flag. What matters is not that a
        // column says inactive, it is that login refuses — and login is the thing an attacker
        // would actually call.
        var auth = BuildAuthService(harness.Context);

        // Still refused, and still an UnauthorizedAccessException to every caller that does not
        // look closer — the API answers 401 exactly as before. The portal does look, and tells
        // the owner to confirm their email rather than that their correct password is wrong.
        var refused = await Assert.ThrowsAsync<SignInRefusedException>(
            () => auth.LoginAsync(new LoginRequest(email, GoodPassword)));
        Assert.IsAssignableFrom<UnauthorizedAccessException>(refused);
        Assert.Equal(SignInRefusal.EmailNotVerified, refused.Reason);
    }

    [SkippableFact]
    public async Task VerifyingTheEmailActivatesTheOwnerAndLetsThemSignIn()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var email = UniqueEmail();

        await harness.Service.SignUpAsync(Request(email));
        var token = TokenFrom(Assert.Single(harness.Email.Sent));

        var result = await harness.Service.VerifyEmailAsync(token);

        Assert.True(result.Succeeded);

        var owner = await harness.Context.Users.SingleAsync(u => u.Email == email);
        Assert.True(owner.IsActive);
        Assert.True(owner.EmailVerified);

        // The password set at signup must authenticate through AuthService. These are two
        // different classes hashing the same password, and if their parameters ever diverge
        // this is the test that notices.
        var auth = BuildAuthService(harness.Context);
        var session = await auth.LoginAsync(new LoginRequest(email, GoodPassword));
        Assert.False(string.IsNullOrWhiteSpace(session.AccessToken));
    }

    // ─── The token ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheTokenIsStoredOnlyAsAHash()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var email = UniqueEmail();

        await harness.Service.SignUpAsync(Request(email));
        var token = TokenFrom(Assert.Single(harness.Email.Sent));

        var stored = await harness.Context.Users
            .Where(u => u.Email == email)
            .Select(u => u.EmailVerificationTokenHash)
            .SingleAsync();

        // A copy of this table must not let anyone activate accounts they were never emailed
        // about.
        Assert.NotNull(stored);
        Assert.NotEqual(token, stored);
        Assert.DoesNotContain(token, stored!, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ATokenWorksOnceAndThenStopsWorking()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);

        await harness.Service.SignUpAsync(Request(UniqueEmail()));
        var token = TokenFrom(Assert.Single(harness.Email.Sent));

        Assert.True((await harness.Service.VerifyEmailAsync(token)).Succeeded);

        // A verification link sits in a mailbox forever. Someone who reads that mailbox later
        // must not be able to replay it.
        var second = await harness.Service.VerifyEmailAsync(token);
        Assert.Equal(EmailVerificationOutcome.InvalidOrAlreadyUsed, second.Outcome);
    }

    [SkippableFact]
    public async Task AnExpiredTokenIsRefusedButSaysSo()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var email = UniqueEmail();

        await harness.Service.SignUpAsync(Request(email));
        var token = TokenFrom(Assert.Single(harness.Email.Sent));

        var owner = await harness.Context.Users.SingleAsync(u => u.Email == email);
        owner.EmailVerificationExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await harness.Context.SaveChangesAsync();

        var result = await harness.Service.VerifyEmailAsync(token);

        // Distinct from invalid, because the person can act on it: expired means ask for
        // another, and telling them it was invalid would send them to support instead.
        Assert.Equal(EmailVerificationOutcome.Expired, result.Outcome);

        var after = await harness.Context.Users.AsNoTracking().SingleAsync(u => u.Email == email);
        Assert.False(after.IsActive);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    public async Task AGarbageTokenIsRefused(string token)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);

        var result = await harness.Service.VerifyEmailAsync(token);

        Assert.Equal(EmailVerificationOutcome.InvalidOrAlreadyUsed, result.Outcome);
    }

    [SkippableFact]
    public async Task EachSignUpGetsADifferentToken()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);

        await harness.Service.SignUpAsync(Request(UniqueEmail()));
        await harness.Service.SignUpAsync(Request(UniqueEmail()));

        Assert.Equal(2, harness.Email.Sent.Count);
        Assert.NotEqual(TokenFrom(harness.Email.Sent[0]), TokenFrom(harness.Email.Sent[1]));
    }

    // ─── Duplicate addresses, without an enumeration oracle ──────────────────

    [SkippableFact]
    public async Task ASecondSignUpOnTheSameAddressCreatesNothing()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var email = UniqueEmail();

        await harness.Service.SignUpAsync(Request(email));
        var organizationsAfterFirst = await harness.Context.Organizations.CountAsync();

        await harness.Service.SignUpAsync(
            new SignUpRequest("Someone Else Ltd", "Impostor", email, GoodPassword));

        Assert.Equal(organizationsAfterFirst, await harness.Context.Organizations.CountAsync());
        Assert.Single(await harness.Context.Users.Where(u => u.Email == email).ToListAsync());
    }

    [SkippableFact]
    public async Task ASecondSignUpLooksIdenticalToTheCallerButNotToTheMailbox()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var email = UniqueEmail();

        var first = await harness.Service.SignUpAsync(Request(email));
        var second = await harness.Service.SignUpAsync(
            new SignUpRequest("Someone Else Ltd", "Impostor", email, GoodPassword));

        // Identical, so the form cannot be used to discover which addresses have accounts.
        Assert.Equal(first.Outcome, second.Outcome);

        // The difference goes to the address itself, where only its owner can read it.
        Assert.Equal(2, harness.Email.Sent.Count);
        Assert.Contains("already", harness.Email.Sent[1].Subject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token=", harness.Email.Sent[1].TextBody, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AddressesAreMatchedWithoutRegardToCaseOrSurroundingSpace()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var email = UniqueEmail();

        await harness.Service.SignUpAsync(Request(email));
        await harness.Service.SignUpAsync(Request($"  {email.ToUpperInvariant()}  "));

        // Otherwise the uniqueness check is trivially bypassed and one address ends up owning
        // two organizations, which login then has to choose between.
        Assert.Single(await harness.Context.Users.Where(u => u.Email == email).ToListAsync());
    }

    [SkippableFact]
    public async Task ADoubleClickedSubmitButtonStillCreatesOneOrganization()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var email = UniqueEmail();

        // Separate harnesses, so each request has its own DbContext and its own transaction —
        // which is what two simultaneous HTTP requests actually get. Sharing one context would
        // serialise them through EF and prove nothing.
        using var first = new Harness(_postgres);
        using var second = new Harness(_postgres);

        using var barrier = new Barrier(2);

        // Task.Run, because Barrier.SignalAndWait blocks the calling thread. Called directly,
        // the first participant would block before the second was ever created, and the test
        // would hang rather than test anything.
        Task<SignUpResult> Submit(Harness harness) => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await harness.Service.SignUpAsync(Request(email));
        });

        var results = await Task.WhenAll(Submit(first), Submit(second));

        Assert.Equal(2, results.Length);

        // The real scenario is not an attack, it is somebody double-clicking. Without the
        // advisory lock both requests find nothing and both create an organization, and the
        // database cannot object: Email is unique per organization, and these are two
        // different organizations.
        using var check = new Harness(_postgres);
        var owners = await check.Context.Users.Where(u => u.Email == email).ToListAsync();
        Assert.Single(owners);

        var organizations = await check.Context.Organizations
            .Where(o => o.Email == email)
            .ToListAsync();
        Assert.Single(organizations);
    }

    // ─── Input rules ─────────────────────────────────────────────────────────

    [SkippableTheory]
    [InlineData("short")]
    [InlineData("alllowercaseletters")]
    [InlineData("")]
    public async Task AWeakPasswordIsRefused(string password)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.SignUpAsync(
            new SignUpRequest("A Business", "A Person", UniqueEmail(), password)));

        Assert.Empty(harness.Email.Sent);
    }

    [SkippableTheory]
    [InlineData("not-an-address")]
    [InlineData("@example.com")]
    [InlineData("two@at@example.com")]
    [InlineData("no-dot@example")]
    public async Task AnUnusableAddressIsRefused(string email)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.SignUpAsync(
            new SignUpRequest("A Business", "A Person", email, GoodPassword)));
    }

    [SkippableTheory]
    [InlineData("", "A Person")]
    [InlineData("   ", "A Person")]
    [InlineData("A Business", "")]
    public async Task ABlankBusinessOrPersonIsRefused(string business, string person)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.SignUpAsync(
            new SignUpRequest(business, person, UniqueEmail(), GoodPassword)));
    }

    [SkippableFact]
    public async Task NothingIsWrittenWhenTheRequestIsRejected()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var before = await harness.Context.Organizations.CountAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.SignUpAsync(
            new SignUpRequest("A Business", "A Person", UniqueEmail(), "weak")));

        Assert.Equal(before, await harness.Context.Organizations.CountAsync());
    }

    // ─── The deployment switch ───────────────────────────────────────────────

    [SkippableFact]
    public async Task SignUpIsRefusedWhenTheDeploymentHasNotEnabledIt()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(
            _postgres,
            options => options.Enabled = false,
            portal => portal.PublicBaseUrl = string.Empty);

        // Opening a financial system to public registration is a decision a deployment makes
        // on purpose, not one it inherits from a default.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.SignUpAsync(Request(UniqueEmail())));

        Assert.Empty(harness.Email.Sent);
    }

    // ─── What the new owner may and may not do ───────────────────────────────

    [SkippableFact]
    public async Task TheNewOwnerIsNotAPlatformAdministrator()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var email = UniqueEmail();

        await harness.Service.SignUpAsync(Request(email));

        var owner = await harness.Context.Users.Include(u => u.Roles).SingleAsync(u => u.Email == email);
        var roles = owner.Roles.Select(r => r.Name).ToList();

        // Signing up must grant authority over one's own business and nothing beyond it.
        // PLATFORM_ADMIN can reach every tenant, and it is reachable here only if someone
        // wires it up by mistake — so it is asserted rather than assumed.
        Assert.DoesNotContain(ZaziRoles.PlatformAdmin, roles);
        Assert.Equal(new[] { ZaziRoles.Owner }, roles);
    }

    [SkippableFact]
    public async Task EachSignUpLandsInItsOwnTenant()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var first = UniqueEmail();
        var second = UniqueEmail();

        await harness.Service.SignUpAsync(Request(first));
        await harness.Service.SignUpAsync(Request(second));

        var a = await harness.Context.Users.SingleAsync(u => u.Email == first);
        var b = await harness.Context.Users.SingleAsync(u => u.Email == second);

        Assert.NotEqual(a.OrganizationId, b.OrganizationId);

        // And the roles are per-tenant rows, not one shared row two organizations point at.
        var roleOrganizations = await harness.Context.Roles
            .Where(r => r.OrganizationId == a.OrganizationId || r.OrganizationId == b.OrganizationId)
            .Select(r => r.OrganizationId)
            .Distinct()
            .CountAsync();

        Assert.Equal(2, roleOrganizations);
    }

    // ─── When the email cannot be sent ───────────────────────────────────────

    [SkippableFact]
    public async Task AFailedEmailStillLeavesARecoverableAccount()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        harness.Email.FailEverything = true;
        var email = UniqueEmail();

        var result = await harness.Service.SignUpAsync(Request(email));

        // Reported, so the page can offer to resend rather than tell someone to check an inbox
        // that will stay empty.
        Assert.Equal(SignUpOutcome.CreatedButEmailFailed, result.Outcome);

        // And the organization survives. Rolling it back would lose the tenant over a
        // transient delivery problem.
        Assert.NotNull(await harness.Context.Users.SingleOrDefaultAsync(u => u.Email == email));
    }

    // ─── Resending ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ResendingIssuesAFreshTokenAndRetiresTheOldOne()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var email = UniqueEmail();

        await harness.Service.SignUpAsync(Request(email));
        var original = TokenFrom(harness.Email.Sent[0]);

        await harness.Service.ResendVerificationAsync(email);
        var replacement = TokenFrom(harness.Email.Sent[1]);

        Assert.NotEqual(original, replacement);

        // The old link stops working the moment a replacement is issued. People ask for a new
        // link when they think the first went astray — which is exactly when the first should
        // stop being usable.
        Assert.Equal(
            EmailVerificationOutcome.InvalidOrAlreadyUsed,
            (await harness.Service.VerifyEmailAsync(original)).Outcome);

        Assert.True((await harness.Service.VerifyEmailAsync(replacement)).Succeeded);
    }

    [SkippableFact]
    public async Task ResendingIsThrottled()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres, options => options.ResendCooldownMinutes = 60);
        var email = UniqueEmail();

        await harness.Service.SignUpAsync(Request(email));
        var afterSignUp = harness.Email.Sent.Count;

        await harness.Service.ResendVerificationAsync(email);

        // Otherwise this form is a way to have Zazi send unlimited mail to an address chosen
        // by whoever is asking, which costs the sending domain its reputation.
        Assert.Equal(afterSignUp, harness.Email.Sent.Count);
    }

    [SkippableFact]
    public async Task ResendingSaysNothingAboutAddressesItDoesNotKnow()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);

        // No exception, no return value, no email. Any of those would make this the
        // enumeration oracle the signup form is careful not to be.
        await harness.Service.ResendVerificationAsync(UniqueEmail());

        Assert.Empty(harness.Email.Sent);
    }

    [SkippableFact]
    public async Task ResendingDoesNothingForAnAccountThatIsAlreadyVerified()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);
        var email = UniqueEmail();

        await harness.Service.SignUpAsync(Request(email));
        await harness.Service.VerifyEmailAsync(TokenFrom(harness.Email.Sent[0]));
        var afterVerification = harness.Email.Sent.Count;

        await harness.Service.ResendVerificationAsync(email);

        Assert.Equal(afterVerification, harness.Email.Sent.Count);
    }

    // ─── The link that gets sent ─────────────────────────────────────────────

    [SkippableFact]
    public async Task TheVerificationLinkPointsAtTheConfiguredPublicUrl()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);

        await harness.Service.SignUpAsync(Request(UniqueEmail()));
        var message = Assert.Single(harness.Email.Sent);

        // Built from configuration, never from a request header. A link assembled from the
        // Host header is one an attacker can point at a site they control — and this link
        // activates an account.
        Assert.Contains("https://app.example.com/verify-email?token=", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://app.example.com/verify-email?token=", message.HtmlBody, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TheBusinessNameIsEncodedBeforeItReachesTheEmail()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var harness = new Harness(_postgres);

        await harness.Service.SignUpAsync(new SignUpRequest(
            "<script>alert('x')</script>", "A Person", UniqueEmail(), GoodPassword));

        var message = Assert.Single(harness.Email.Sent);

        // The business name is chosen by whoever signs up, and it lands in HTML sent to an
        // address they also chose.
        Assert.DoesNotContain("<script>", message.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", message.HtmlBody, StringComparison.Ordinal);
    }

    private static AuthService BuildAuthService(ApplicationDbContext context) =>
        new(
            context,
            Microsoft.Extensions.Options.Options.Create(new JwtOptions
            {
                Key = "integration-test-signing-key-at-least-32-bytes-long"
            }),
            NullLogger<AuthService>.Instance);
}
