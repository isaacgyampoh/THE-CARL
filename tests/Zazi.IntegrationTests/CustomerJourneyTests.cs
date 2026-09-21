extern alias portal;

using System.Net;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Zazi.Application.Email;
using Zazi.Domain;
using Zazi.IntegrationTests.Postgres;

namespace Zazi.IntegrationTests;

/// <summary>
/// The whole journey a customer actually takes, over real HTTP.
/// </summary>
/// <remarks>
/// <para>
/// Sign up, open the link in the email, sign in, reach the portal; then forget the password,
/// reset it, and sign in again. Every step is a real request against a real database, with a
/// real cookie carried between them.
/// </para>
/// <para>
/// This exists because the pieces were all individually tested and the journey still did not
/// work: the services were tested by direct call, the pages by GET, and the one step in
/// between — submitting a form — was covered by neither. A duplicated antiforgery field made
/// every submission return 400, and nothing noticed. A test that walks the whole path is the
/// only kind that would have.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class CustomerJourneyTests
{
    private const string Password = "Correct-Horse-9-Battery";
    private const string NewPassword = "Replacement-7-Secret-Key";

    private readonly PostgresFixture _postgres;

    public CustomerJourneyTests(PostgresFixture postgres) => _postgres = postgres;

    /// <summary>Holds the mail the application tried to send, so a test can open the links.</summary>
    private sealed class Inbox : IEmailSender
    {
        private readonly List<EmailMessage> _messages = new();

        public IReadOnlyList<EmailMessage> Messages
        {
            get { lock (_messages) { return _messages.ToList(); } }
        }

        public Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            lock (_messages) { _messages.Add(message); }
            return Task.FromResult(EmailResult.Success());
        }

        /// <summary>The token from the most recent message containing one.</summary>
        public string LatestToken()
        {
            var message = Messages.LastOrDefault(m => m.TextBody.Contains("token=", StringComparison.Ordinal));
            Assert.NotNull(message);

            var match = Regex.Match(message!.TextBody, @"token=([^\s]+)");
            Assert.True(match.Success, "No token in the email body.");
            return Uri.UnescapeDataString(match.Groups[1].Value);
        }
    }

    private sealed class PortalFactory : WebApplicationFactory<portal::Program>
    {
        private readonly string _connectionString;
        private readonly Inbox _inbox;

        public PortalFactory(string connectionString, Inbox inbox)
        {
            _connectionString = connectionString;
            _inbox = inbox;
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Production");

            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zazi:DataProtectionKeyPath"] = Path.Combine(Path.GetTempPath(), "zazi-journey-keys"),
                    ["ConnectionStrings:DefaultConnection"] = _connectionString,
                    ["Zazi:BehindTlsProxy"] = "true",
                    ["Jwt:Key"] = "integration-test-signing-key-at-least-32-bytes-long",
                    ["Portal:PublicBaseUrl"] = "https://app.example.com",
                    ["SignUp:Enabled"] = "true",
                    ["PasswordReset:MinimumResponseMilliseconds"] = "0",
                    ["PasswordReset:RequestCooldownMinutes"] = "0",
                    // Signing up has just sent a link; a resend inside the cooldown is refused on
                    // purpose. The test is about the button working, not about that timing.
                    ["SignUp:ResendCooldownMinutes"] = "0"
                }));

            // The only substitution. Everything else is the real application.
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(_inbox);
            });

            return base.CreateHost(builder);
        }
    }

    /// <summary>A client that keeps cookies, like a browser.</summary>
    /// <remarks>
    /// The https base address is load-bearing, not decoration. Outside Development the session
    /// cookie is issued with <c>Secure</c>, so a client talking to <c>http://localhost</c>
    /// stores it and then never sends it back — sign-in appears to succeed and every page after
    /// it redirects to sign-in again. That is correct cookie behaviour and the right production
    /// setting; the test simply has to speak the scheme the cookie was issued for.
    /// </remarks>
    private static HttpClient Browser(WebApplicationFactory<portal::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost")
        });

    private static List<KeyValuePair<string, string>> HiddenFields(string html)
    {
        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match match in Regex.Matches(html, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            var name = Regex.Match(match.Value, "name=\"([^\"]+)\"").Groups[1].Value;
            var value = Regex.Match(match.Value, "value=\"([^\"]*)\"").Groups[1].Value;
            if (name.Length > 0)
            {
                fields.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        return fields;
    }

    /// <summary>Posts a page's form back to it, carrying whatever hidden fields it rendered.</summary>
    private static async Task<HttpResponseMessage> SubmitAsync(
        HttpClient client,
        string path,
        params KeyValuePair<string, string>[] values)
    {
        var html = await client.GetStringAsync(path);
        var fields = HiddenFields(html);
        fields.AddRange(values);
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    private static string UniqueEmail() => $"journey-{Guid.NewGuid():N}@example.com";

    private static KeyValuePair<string, string> Field(string name, string value) => new(name, value);

    // ─── Signing up and getting in ───────────────────────────────────────────

    [SkippableFact]
    public async Task ACustomerCanSignUpVerifyTheirEmailAndReachThePortal()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var inbox = new Inbox();
        using var factory = new PortalFactory(_postgres.ConnectionString!, inbox);
        using var client = Browser(factory);
        var email = UniqueEmail();

        // 1. Sign up.
        using (var signUp = await SubmitAsync(client, "/sign-up",
            Field("_input.BusinessName", "Journey Mobile Money"),
            Field("_input.FullName", "Journey Owner"),
            Field("_input.Email", email),
            Field("_input.Password", Password),
            Field("_input.ConfirmPassword", Password)))
        {
            Assert.Equal(HttpStatusCode.OK, signUp.StatusCode);
            Assert.Contains("Check your email", await signUp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // 2. The account exists but must not be usable yet. This is the rule the whole flow is
        //    built around, checked here through the front door rather than against a column.
        //
        //    It used to answer "invalid" — the same as a wrong password — and real owners who
        //    had just signed up were told their correct password was wrong. With the right
        //    password, saying "confirm your email first" discloses nothing.
        using (var tooEarly = await client.PostAsync("/auth/sign-in", await SignInFormAsync(client, email, Password)))
        {
            Assert.Equal(HttpStatusCode.Redirect, tooEarly.StatusCode);
            Assert.Contains("error=unverified", tooEarly.Headers.Location!.ToString(), StringComparison.Ordinal);
        }

        // 3. Open the link from the email.
        using (var verify = await client.GetAsync($"/verify-email?token={Uri.EscapeDataString(inbox.LatestToken())}"))
        {
            Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
            Assert.Contains("Email confirmed", await verify.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // 4. Now sign in.
        using (var signIn = await client.PostAsync("/auth/sign-in", await SignInFormAsync(client, email, Password)))
        {
            Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
            Assert.Equal("/", signIn.Headers.Location!.ToString());
        }

        // 5. And the portal is actually reachable, with the cookie the sign-in issued.
        using var dashboard = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
    }

    [SkippableFact]
    public async Task AVerificationLinkCannotBeUsedTwice()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var inbox = new Inbox();
        using var factory = new PortalFactory(_postgres.ConnectionString!, inbox);
        using var client = Browser(factory);
        var email = UniqueEmail();

        await SubmitAsync(client, "/sign-up",
            Field("_input.BusinessName", "Replay Test"),
            Field("_input.FullName", "Replay Owner"),
            Field("_input.Email", email),
            Field("_input.Password", Password),
            Field("_input.ConfirmPassword", Password));

        var token = inbox.LatestToken();
        var link = $"/verify-email?token={Uri.EscapeDataString(token)}";

        Assert.Contains("Email confirmed", await client.GetStringAsync(link), StringComparison.Ordinal);

        // A verification link sits in a mailbox forever. Whoever reads that mailbox later must
        // not be able to replay it.
        Assert.DoesNotContain("Email confirmed", await client.GetStringAsync(link), StringComparison.Ordinal);
    }

    // ─── Forgetting and resetting the password ───────────────────────────────

    [SkippableFact]
    public async Task ACustomerCanResetAForgottenPasswordAndSignInWithTheNewOne()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var inbox = new Inbox();
        using var factory = new PortalFactory(_postgres.ConnectionString!, inbox);
        using var client = Browser(factory);
        var email = UniqueEmail();

        // Get to a usable account first.
        await SubmitAsync(client, "/sign-up",
            Field("_input.BusinessName", "Reset Journey Ltd"),
            Field("_input.FullName", "Reset Owner"),
            Field("_input.Email", email),
            Field("_input.Password", Password),
            Field("_input.ConfirmPassword", Password));
        await client.GetAsync($"/verify-email?token={Uri.EscapeDataString(inbox.LatestToken())}");

        // 1. Ask for a reset.
        using (var requested = await SubmitAsync(client, "/forgot-password", Field("_input.Email", email)))
        {
            Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
            Assert.Contains("Check your email", await requested.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var resetLink = $"/reset-password?token={Uri.EscapeDataString(inbox.LatestToken())}";

        // 2. The link offers a form.
        Assert.Contains("Choose a new password", await client.GetStringAsync(resetLink), StringComparison.Ordinal);

        // 3. Set the new password.
        using (var reset = await SubmitAsync(client, resetLink,
            Field("_input.Password", NewPassword),
            Field("_input.Confirm", NewPassword)))
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
            Assert.Contains("Password changed", await reset.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // 4. The old password is dead.
        using (var old = await client.PostAsync("/auth/sign-in", await SignInFormAsync(client, email, Password)))
        {
            Assert.Contains("error=invalid", old.Headers.Location!.ToString(), StringComparison.Ordinal);
        }

        // 5. The new one works.
        using var fresh = await client.PostAsync("/auth/sign-in", await SignInFormAsync(client, email, NewPassword));
        Assert.Equal("/", fresh.Headers.Location!.ToString());
    }

    [SkippableFact]
    public async Task AResetLinkCannotBeUsedTwice()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var inbox = new Inbox();
        using var factory = new PortalFactory(_postgres.ConnectionString!, inbox);
        using var client = Browser(factory);
        var email = UniqueEmail();

        await SubmitAsync(client, "/sign-up",
            Field("_input.BusinessName", "Reset Replay Ltd"),
            Field("_input.FullName", "Reset Replay"),
            Field("_input.Email", email),
            Field("_input.Password", Password),
            Field("_input.ConfirmPassword", Password));
        await client.GetAsync($"/verify-email?token={Uri.EscapeDataString(inbox.LatestToken())}");

        await SubmitAsync(client, "/forgot-password", Field("_input.Email", email));
        var resetLink = $"/reset-password?token={Uri.EscapeDataString(inbox.LatestToken())}";

        await SubmitAsync(client, resetLink,
            Field("_input.Password", NewPassword),
            Field("_input.Confirm", NewPassword));

        // Replaying the link must not offer the form again.
        var second = await client.GetStringAsync(resetLink);
        Assert.DoesNotContain("Choose a new password</p>", second, StringComparison.Ordinal);
        Assert.Contains("did not work", second, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Neither form reveals who has an account ─────────────────────────────

    [SkippableFact]
    public async Task ForgottenPasswordLooksTheSameForAKnownAndAnUnknownAddress()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var inbox = new Inbox();
        using var factory = new PortalFactory(_postgres.ConnectionString!, inbox);
        using var client = Browser(factory);
        var known = UniqueEmail();

        await SubmitAsync(client, "/sign-up",
            Field("_input.BusinessName", "Enumeration Test"),
            Field("_input.FullName", "Enumeration Owner"),
            Field("_input.Email", known),
            Field("_input.Password", Password),
            Field("_input.ConfirmPassword", Password));
        await client.GetAsync($"/verify-email?token={Uri.EscapeDataString(inbox.LatestToken())}");

        var unknown = UniqueEmail();
        using var forKnown = await SubmitAsync(client, "/forgot-password", Field("_input.Email", known));
        using var forUnknown = await SubmitAsync(client, "/forgot-password", Field("_input.Email", unknown));

        Assert.Equal(forKnown.StatusCode, forUnknown.StatusCode);

        var knownBody = Normalise(await forKnown.Content.ReadAsStringAsync(), known);
        var unknownBody = Normalise(await forUnknown.Content.ReadAsStringAsync(), unknown);

        // Byte-for-byte identical once the two things that legitimately differ between any
        // two renders are masked: the address the page echoes back, and the antiforgery token.
        // Anything remaining — a different word, an extra line, a changed length — is a way to
        // test which addresses have accounts.
        Assert.Equal(unknownBody, knownBody);
        Assert.Contains("Check your email", knownBody, StringComparison.Ordinal);

        // Masking could in principle hide a difference in how much was masked, so the raw
        // lengths are compared too. Together these say: same content, and the same amount of it.
        Assert.Equal(
            (await forUnknown.Content.ReadAsStringAsync()).Replace(unknown, "ADDRESS", StringComparison.Ordinal).Length,
            (await forKnown.Content.ReadAsStringAsync()).Replace(known, "ADDRESS", StringComparison.Ordinal).Length);
    }

    [SkippableFact]
    public async Task SignUpLooksTheSameForAFreshAndAnAlreadyRegisteredAddress()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var inbox = new Inbox();
        using var factory = new PortalFactory(_postgres.ConnectionString!, inbox);
        using var client = Browser(factory);
        var email = UniqueEmail();

        using var first = await SubmitAsync(client, "/sign-up",
            Field("_input.BusinessName", "First Business"),
            Field("_input.FullName", "First Owner"),
            Field("_input.Email", email),
            Field("_input.Password", Password),
            Field("_input.ConfirmPassword", Password));

        using var second = await SubmitAsync(client, "/sign-up",
            Field("_input.BusinessName", "Second Business"),
            Field("_input.FullName", "Impostor"),
            Field("_input.Email", email),
            Field("_input.Password", Password),
            Field("_input.ConfirmPassword", Password));

        Assert.Equal(first.StatusCode, second.StatusCode);

        var firstBody = await first.Content.ReadAsStringAsync();
        var secondBody = await second.Content.ReadAsStringAsync();

        // Same confirmation both times. The difference goes to the mailbox, where only the
        // person who owns the address can read it.
        Assert.Contains("Check your email", firstBody, StringComparison.Ordinal);
        Assert.Contains("Check your email", secondBody, StringComparison.Ordinal);

        var latest = inbox.Messages.Last();
        Assert.Contains("already", latest.Subject, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the sign-in post, taking a fresh antiforgery token from the page each time.
    /// </summary>
    private static async Task<FormUrlEncodedContent> SignInFormAsync(HttpClient client, string email, string password)
    {
        var html = await client.GetStringAsync("/sign-in");
        var fields = HiddenFields(html);
        fields.Add(new KeyValuePair<string, string>("email", email));
        fields.Add(new KeyValuePair<string, string>("password", password));
        return new FormUrlEncodedContent(fields);
    }

    /// <summary>
    /// Blanks the parts of a response that differ between any two renders, so what is left can
    /// be compared for equality.
    /// </summary>
    private static string Normalise(string html, string address) =>
        // Every input value, not just the antiforgery token. The page also carries Blazor's own
        // per-render form state, which is random too — masking by prefix meant chasing each new
        // kind of token as it appeared. What matters is that nothing outside these values
        // differs, and blanking all of them states exactly that.
        Regex.Replace(
            Regex.Replace(
                html.Replace(address, "ADDRESS", StringComparison.Ordinal),
                "(<input[^>]*?value=\")[^\"]*(\")",
                "$1MASKED$2"),
            // Blazor's persisted component state, encrypted with the data-protection key and
            // therefore different on every single render whatever the page said.
            "(<!--Blazor-Server-Component-State:)[^-]*(-->)",
            "$1MASKED$2");

    [SkippableFact]
    public async Task AStrangerCanGoFromNothingToIssuingAnActivationCodeUnaided()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        // The whole path a test user takes with nobody helping them: sign up, confirm the
        // email, sign in, add a branch, add a worker. Every step over real HTTP with a real
        // cookie, because the two bugs that blocked activation — an unbound form model and a
        // dead @onclick handler — were both invisible to every other kind of test.
        var inbox = new Inbox();
        using var factory = new PortalFactory(_postgres.ConnectionString!, inbox);
        using var client = Browser(factory);
        var email = UniqueEmail();

        // 1. Sign up.
        using (var r = await SubmitAsync(client, "/sign-up",
            Field("_input.BusinessName", "Stranger Mobile Money"),
            Field("_input.FullName", "A Stranger"),
            Field("_input.Email", email),
            Field("_input.Password", Password),
            Field("_input.ConfirmPassword", Password)))
        {
            Assert.Contains("Check your email", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // 2. Confirm the address.
        Assert.Contains("Email confirmed",
            await client.GetStringAsync($"/verify-email?token={Uri.EscapeDataString(inbox.LatestToken())}"),
            StringComparison.Ordinal);

        // 3. Sign in and reach the portal.
        using (var r = await client.PostAsync("/auth/sign-in", await SignInFormAsync(client, email, Password)))
        {
            Assert.Equal("/", r.Headers.Location?.ToString());
        }
        using (var r = await client.GetAsync("/"))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        // 4. The Team page is reachable, with the branch signup created.
        var team = await client.GetStringAsync("/team");
        Assert.Contains("Main branch", team, StringComparison.Ordinal);
        Assert.Contains("Add a worker", team, StringComparison.Ordinal);

        // 5. Add a second branch.
        var branchForm = HiddenFields(team, "add-branch");
        branchForm.Add(Field("_newBranch.Name", "Kumasi"));
        branchForm.Add(Field("_newBranch.Location", "Adum"));
        using (var r = await client.PostAsync("/team", new FormUrlEncodedContent(branchForm)))
        {
            Assert.Contains("Kumasi", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // 6. Add a worker — the step that was refusing every submission in production.
        team = await client.GetStringAsync("/team");
        var branchId = await BranchIdAsync(email, "Main branch");
        var workerForm = HiddenFields(team, "add-worker");
        workerForm.Add(Field("_newWorker.FullName", "Yaw Agent"));
        workerForm.Add(Field("_newWorker.BranchId", branchId));

        using (var r = await client.PostAsync("/team", new FormUrlEncodedContent(workerForm)))
        {
            var html = await r.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Choose a branch for this worker", html, StringComparison.Ordinal);
            Assert.Contains("Yaw Agent", html, StringComparison.Ordinal);
        }

        // 7. The worker is a real activation-only identity, ready for a code.
        await using var db = _postgres.CreateContext();
        var worker = db.Users.Single(u => u.FullName == "Yaw Agent");
        Assert.Equal(UserCredentialType.ActivationOnly, worker.CredentialType);
        Assert.False(string.IsNullOrEmpty(worker.BranchId?.ToString()));
    }

    /// <summary>The id of a named branch in the organization that owns an address.</summary>
    private async Task<string> BranchIdAsync(string ownerEmail, string branchName)
    {
        await using var db = _postgres.CreateContext();
        var organizationId = db.Users.Single(u => u.Email == ownerEmail).OrganizationId;
        return db.Branches.Single(b => b.OrganizationId == organizationId && b.Name == branchName).Id.ToString();
    }

    private static List<KeyValuePair<string, string>> HiddenFields(string html, string formName)
    {
        var scope = html;
        var marker = html.IndexOf($"value=\"{formName}\"", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var start = html.LastIndexOf("<form", marker, StringComparison.Ordinal);
            var end = html.IndexOf("</form>", marker, StringComparison.Ordinal);
            if (start >= 0 && end > start) scope = html[start..end];
        }

        return HiddenFields(scope);
    }

    // ─── The "incorrect password" reports ────────────────────────────────────
    //
    // Three owners signed up on 20–21 Sep, confirmed their email, and were told their password
    // was incorrect. The logs showed the account was found every time ("1-candidate") and the
    // password check itself failed — and one of them had signed up a second time moments
    // before, which kept their first password and silently discarded the one they then tried.
    // Each test below is one of the ways a person ended up there, and the way back out.

    private const string ThirdPassword = "Third-Attempt-4-Works";

    private static async Task SignUpAsync(HttpClient client, string email, string password, string? confirm = null)
    {
        using var response = await SubmitAsync(client, "/sign-up",
            Field("_input.BusinessName", "Recovery Journey Ltd"),
            Field("_input.FullName", "Recovery Owner"),
            Field("_input.Email", email),
            Field("_input.Password", password),
            Field("_input.ConfirmPassword", confirm ?? password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The last link in the inbox that points at <paramref name="path"/>.</summary>
    private static string LatestLink(Inbox inbox, string path)
    {
        var message = inbox.Messages.LastOrDefault(m => m.TextBody.Contains(path, StringComparison.Ordinal));
        Assert.True(message is not null, $"No email contains a link to {path}.");

        var match = Regex.Match(message!.TextBody, Regex.Escape(path) + @"[^\s]*");
        return new Uri(match.Value).PathAndQuery;
    }

    private static async Task<string> SignInAsync(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsync("/auth/sign-in", await SignInFormAsync(client, email, password));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.ToString();
    }

    private static async Task ResetPasswordAsync(HttpClient client, Inbox inbox, string email, string newPassword)
    {
        var before = inbox.Messages.Count;
        using (await SubmitAsync(client, "/forgot-password", Field("_input.Email", email)))
        {
        }

        Assert.True(inbox.Messages.Count > before, "Asking for a reset sent nothing.");
        var link = LatestLink(inbox, "https://app.example.com/reset-password");

        using var reset = await SubmitAsync(client, link,
            Field("_input.Password", newPassword),
            Field("_input.Confirm", newPassword));
        Assert.Contains("Password changed", await reset.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task SigningUpAgainKeepsTheFirstPasswordAndSaysHowToGetBackIn()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var inbox = new Inbox();
        using var factory = new PortalFactory(_postgres.ConnectionString!, inbox);
        using var client = Browser(factory);
        var email = UniqueEmail();

        await SignUpAsync(client, email, Password);
        await client.GetAsync($"/verify-email?token={Uri.EscapeDataString(inbox.LatestToken())}");

        // What the tester did: signed up again, with a different password.
        using (var again = await SubmitAsync(client, "/sign-up",
            Field("_input.BusinessName", "Recovery Journey Ltd"),
            Field("_input.FullName", "Recovery Owner"),
            Field("_input.Email", email),
            Field("_input.Password", NewPassword),
            Field("_input.ConfirmPassword", NewPassword)))
        {
            // The screen everyone sees names the rule. Shown for fresh addresses too, so it
            // reveals nothing about which this was.
            var html = await again.Content.ReadAsStringAsync();
            Assert.Contains("Signed up with this address before?", html, StringComparison.Ordinal);
            Assert.Contains("original password still applies", html, StringComparison.Ordinal);
        }

        // The email to the address owner leads with setting a new password — it used to say
        // only "sign in instead", which sent them to try the password that had been discarded.
        var notice = inbox.Messages.Last();
        Assert.Equal("You already have a Zazi account", notice.Subject);
        Assert.Contains("https://app.example.com/forgot-password", notice.TextBody, StringComparison.Ordinal);
        // Whitespace collapsed first: the plain-text body is wrapped for mail clients.
        var body = Regex.Replace(notice.TextBody, @"\s+", " ");
        Assert.Contains("password you just typed was not saved", body, StringComparison.Ordinal);

        // The second password is still not the account's. That is deliberate — letting a second
        // signup change the password would let anyone who knows an address set it — and the
        // sign-in page now says what happened and how out.
        var refused = await SignInAsync(client, email, NewPassword);
        Assert.Contains("error=invalid", refused, StringComparison.Ordinal);
        var page = await client.GetStringAsync(refused);
        Assert.Contains("only your <em>first</em> password was", page, StringComparison.Ordinal);
        Assert.Contains("/forgot-password?email=", page, StringComparison.Ordinal);

        // And the way out works.
        await ResetPasswordAsync(client, inbox, email, ThirdPassword);
        Assert.Equal("/", await SignInAsync(client, email, ThirdPassword));
    }

    [SkippableFact]
    public async Task AnOwnerWhoNeverConfirmedTheirEmailCanStillResetAndGetIn()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var inbox = new Inbox();
        using var factory = new PortalFactory(_postgres.ConnectionString!, inbox);
        using var client = Browser(factory);
        var email = UniqueEmail();

        // Signed up, never opened the link. Before this fix, reset refused inactive accounts and
        // a second signup kept the first password, so this person had no way in at all.
        await SignUpAsync(client, email, Password);

        await ResetPasswordAsync(client, inbox, email, NewPassword);

        // A reset link to the same mailbox proves what the confirmation link would have, so it
        // confirms the address too — otherwise they would hold a new password they could not use.
        Assert.Equal("/", await SignInAsync(client, email, NewPassword));

        await using var db = _postgres.CreateContext();
        var owner = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email);
        Assert.True(owner.EmailVerified);
        Assert.True(owner.IsActive);
        Assert.Null(owner.EmailVerificationTokenHash);
    }

    [SkippableFact]
    public async Task TheRightPasswordBeforeConfirmingSaysSoAndCanResendTheLink()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var inbox = new Inbox();
        using var factory = new PortalFactory(_postgres.ConnectionString!, inbox);
        using var client = Browser(factory);
        var email = UniqueEmail();

        await SignUpAsync(client, email, Password);

        var refused = await SignInAsync(client, email, Password);
        Assert.Contains("error=unverified", refused, StringComparison.Ordinal);

        var page = await client.GetStringAsync(refused);
        Assert.Contains("Your password is right", page, StringComparison.Ordinal);

        // The resend button on that page actually sends a fresh confirmation link.
        var before = inbox.Messages.Count;
        using (var resent = await client.PostAsync(refused, FormFor(page, "resend-from-sign-in")))
        {
            Assert.Equal(HttpStatusCode.OK, resent.StatusCode);
            Assert.Contains("Confirmation link sent", await resent.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.True(inbox.Messages.Count > before, "The resend button sent nothing.");

        await client.GetAsync($"/verify-email?token={Uri.EscapeDataString(inbox.LatestToken())}");
        Assert.Equal("/", await SignInAsync(client, email, Password));
    }

    [SkippableFact]
    public async Task TheRightPasswordOnALockedAccountSaysLockedNotIncorrect()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var inbox = new Inbox();
        using var factory = new PortalFactory(_postgres.ConnectionString!, inbox);
        using var client = Browser(factory);
        var email = UniqueEmail();

        await SignUpAsync(client, email, Password);
        await client.GetAsync($"/verify-email?token={Uri.EscapeDataString(inbox.LatestToken())}");

        // What a confused owner does: keeps trying.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await SignInAsync(client, email, "Not-The-Password-1");
        }

        // The right password now. It used to answer "incorrect", which sent them to try again
        // and extend the lockout.
        var locked = await SignInAsync(client, email, Password);
        Assert.Contains("error=locked", locked, StringComparison.Ordinal);
        Assert.Contains("sign-in is paused", await client.GetStringAsync(locked), StringComparison.Ordinal);

        // Setting a new password lifts it at once.
        await ResetPasswordAsync(client, inbox, email, NewPassword);
        Assert.Equal("/", await SignInAsync(client, email, NewPassword));
    }

    [SkippableFact]
    public async Task AWrongPasswordAndAnUnknownAddressStillReadTheSame()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var inbox = new Inbox();
        using var factory = new PortalFactory(_postgres.ConnectionString!, inbox);
        using var client = Browser(factory);
        var email = UniqueEmail();

        await SignUpAsync(client, email, Password);
        await client.GetAsync($"/verify-email?token={Uri.EscapeDataString(inbox.LatestToken())}");

        // The new messages only appear once the password is right. Without it, "no such account"
        // and "wrong password" must stay one answer, or the form tells anyone which addresses
        // are registered.
        var wrong = await SignInAsync(client, email, "Not-The-Password-1");
        var unknown = await SignInAsync(client, UniqueEmail(), "Not-The-Password-1");

        Assert.Contains("error=invalid", wrong, StringComparison.Ordinal);
        Assert.Contains("error=invalid", unknown, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task MismatchedPasswordsAtSignUpCreateNothing()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var inbox = new Inbox();
        using var factory = new PortalFactory(_postgres.ConnectionString!, inbox);
        using var client = Browser(factory);
        var email = UniqueEmail();

        using (var response = await SubmitAsync(client, "/sign-up",
            Field("_input.BusinessName", "Typo Ltd"),
            Field("_input.FullName", "Typo Owner"),
            Field("_input.Email", email),
            Field("_input.Password", Password),
            Field("_input.ConfirmPassword", Password + "x")))
        {
            Assert.Contains("passwords don&#x27;t match", await response.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }

        // Caught before the account exists, which is the only point it can be fixed: after
        // creation a second signup keeps the first password.
        Assert.Empty(inbox.Messages);
        await using var db = _postgres.CreateContext();
        Assert.False(await db.Users.AnyAsync(u => u.Email == email));
    }

    [SkippableFact]
    public async Task ADeliberatelyDisabledAccountIsNotReopenedByAPasswordReset()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var inbox = new Inbox();
        using var factory = new PortalFactory(_postgres.ConnectionString!, inbox);
        using var client = Browser(factory);
        var email = UniqueEmail();

        await SignUpAsync(client, email, Password);
        await client.GetAsync($"/verify-email?token={Uri.EscapeDataString(inbox.LatestToken())}");

        // Switched off by someone with the authority to. This is the case the old rule was
        // protecting, and the reason reset could not simply accept every inactive account.
        await using (var db = _postgres.CreateContext())
        {
            var owner = await db.Users.SingleAsync(u => u.Email == email);
            owner.IsActive = false;
            await db.SaveChangesAsync();
        }

        var before = inbox.Messages.Count;
        using (await SubmitAsync(client, "/forgot-password", Field("_input.Email", email)))
        {
        }

        Assert.Equal(before, inbox.Messages.Count);
        Assert.Contains("error=disabled", await SignInAsync(client, email, Password), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AThrottledSignInLandsOnTheSignInPageNotABlankError()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var inbox = new Inbox();
        using var factory = new PortalFactory(_postgres.ConnectionString!, inbox);
        using var client = Browser(factory);

        // Past the ten-a-minute allowance for one connection. Several owners behind one mobile
        // carrier's shared address can get here together.
        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            last?.Dispose();
            last = await client.PostAsync("/auth/sign-in",
                await SignInFormAsync(client, UniqueEmail(), "Not-The-Password-1"));
        }

        using (last)
        {
            // A bare 429 renders as an empty error page with no way forward.
            Assert.Equal(HttpStatusCode.SeeOther, last!.StatusCode);
            Assert.Equal("/sign-in?error=busy", last.Headers.Location!.ToString());
        }

        Assert.Contains("Too many sign-in attempts", await client.GetStringAsync("/sign-in?error=busy"),
            StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TodayShowsFloatPerNetworkForANewOwner()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var inbox = new Inbox();
        using var factory = new PortalFactory(_postgres.ConnectionString!, inbox);
        using var client = Browser(factory);
        var email = UniqueEmail();

        await SignUpAsync(client, email, Password);
        await client.GetAsync($"/verify-email?token={Uri.EscapeDataString(inbox.LatestToken())}");
        Assert.Equal("/", await SignInAsync(client, email, Password));

        var html = await client.GetStringAsync("/");

        // The page reads three services now; any of them failing shows the unavailable notice
        // rather than zeroes, so its absence is what proves the reads worked.
        Assert.DoesNotContain("could not be loaded", html, StringComparison.Ordinal);

        // One figure per network. The old single "network float" total could look healthy
        // while one network had run dry.
        Assert.Contains("Money on hand", html, StringComparison.Ordinal);
        foreach (var network in new[] { "MTN float", "Telecel float", "AirtelTigo float" })
        {
            Assert.Contains(network, html, StringComparison.Ordinal);
        }

        // A brand-new business says so in words rather than showing empty boxes.
        Assert.Contains("Nothing needs attention", html, StringComparison.Ordinal);
        Assert.Contains("No transactions yet today", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fields of one form on a page, chosen by its handler name.
    /// </summary>
    /// <remarks>
    /// Not every hidden input on the page. The sign-in page can render two forms, each with its
    /// own antiforgery token, and posting both sends the token twice — which the framework reads
    /// as "token,token" and rejects.
    /// </remarks>
    private static FormUrlEncodedContent FormFor(string html, string handler)
    {
        foreach (Match form in Regex.Matches(html, "<form[^>]*>.*?</form>", RegexOptions.Singleline))
        {
            if (form.Value.Contains($"value=\"{handler}\"", StringComparison.Ordinal))
            {
                var fields = new List<KeyValuePair<string, string>>();
                foreach (Match input in Regex.Matches(form.Value, "<input[^>]*>"))
                {
                    var name = Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value;
                    var value = Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value;
                    if (name.Length > 0)
                    {
                        fields.Add(new KeyValuePair<string, string>(name, System.Net.WebUtility.HtmlDecode(value)));
                    }
                }

                return new FormUrlEncodedContent(fields);
            }
        }

        throw new Xunit.Sdk.XunitException($"No form with handler '{handler}' on the page.");
    }
}
