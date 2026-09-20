extern alias portal;

using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Zazi.Application.Email;
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
                    ["PasswordReset:RequestCooldownMinutes"] = "0"
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
            Field("_input.Password", Password)))
        {
            Assert.Equal(HttpStatusCode.OK, signUp.StatusCode);
            Assert.Contains("Check your email", await signUp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // 2. The account exists but must not be usable yet. This is the rule the whole flow is
        //    built around, checked here through the front door rather than against a column.
        using (var tooEarly = await client.PostAsync("/auth/sign-in", await SignInFormAsync(client, email, Password)))
        {
            Assert.Equal(HttpStatusCode.Redirect, tooEarly.StatusCode);
            Assert.Contains("error=invalid", tooEarly.Headers.Location!.ToString(), StringComparison.Ordinal);
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
            Field("_input.Password", Password));

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
            Field("_input.Password", Password));
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
            Field("_input.Password", Password));
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
            Field("_input.Password", Password));
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
            Field("_input.Password", Password));

        using var second = await SubmitAsync(client, "/sign-up",
            Field("_input.BusinessName", "Second Business"),
            Field("_input.FullName", "Impostor"),
            Field("_input.Email", email),
            Field("_input.Password", Password));

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
}
