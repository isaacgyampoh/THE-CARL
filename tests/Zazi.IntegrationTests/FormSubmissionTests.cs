extern alias portal;

using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Zazi.IntegrationTests.Postgres;

namespace Zazi.IntegrationTests;

/// <summary>
/// That the forms on the anonymous pages can actually be submitted.
/// </summary>
/// <remarks>
/// <para>
/// This exists because they could not. Every form here is an <c>EditForm</c> with a
/// <c>FormName</c>, which emits its own antiforgery field — and each page also carried an
/// explicit <c>&lt;AntiforgeryToken /&gt;</c>. Two hidden inputs of the same name post the
/// value twice, the framework reads it as <c>"token,token"</c>, and every submission came
/// back <c>400</c>. Sign-up, forgot-password, reset-password and the Team page's worker form
/// were all unusable.
/// </para>
/// <para>
/// Nothing caught it. The page tests only ever issued GETs, so they proved the forms
/// <i>rendered</i>; the service tests called the services directly, bypassing HTTP entirely.
/// The gap was exactly the step in between — the one every real user takes.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class FormSubmissionTests
{
    private readonly PostgresFixture _postgres;

    public FormSubmissionTests(PostgresFixture postgres) => _postgres = postgres;

    private sealed class PortalFactory : WebApplicationFactory<portal::Program>
    {
        private readonly string _connectionString;

        public PortalFactory(string connectionString) => _connectionString = connectionString;

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zazi:DataProtectionKeyPath"] = Path.Combine(Path.GetTempPath(), "zazi-form-keys"),
                    ["ConnectionStrings:DefaultConnection"] = _connectionString,
                    ["Zazi:BehindTlsProxy"] = "true",
                    ["Jwt:Key"] = "integration-test-signing-key-at-least-32-bytes-long",
                    ["Portal:PublicBaseUrl"] = "https://app.example.com",
                    ["SignUp:Enabled"] = "true",
                    ["PasswordReset:MinimumResponseMilliseconds"] = "0"
                }));

            return base.CreateHost(builder);
        }
    }

    /// <summary>
    /// Collects the hidden fields a page rendered, the way a browser would.
    /// </summary>
    /// <remarks>
    /// Read from the markup rather than constructed here on purpose: the bug this file exists
    /// for was a duplicated hidden field, and a test that builds its own payload would have
    /// posted one token and passed while every real browser posted two and failed.
    /// </remarks>
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

    private static int TokenCount(string html) =>
        Regex.Matches(html, "name=\"__RequestVerificationToken\"").Count;

    [SkippableTheory]
    [InlineData("/forgot-password")]
    [InlineData("/sign-up")]
    [InlineData("/sign-in")]
    public async Task EachPageRendersExactlyOneAntiforgeryTokenPerForm(string path)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(path);
        var forms = Regex.Matches(html, "<form").Count;

        // One per form, no more. A second is not harmless duplication — it is a 400 on submit.
        Assert.Equal(forms, TokenCount(html));
    }

    [SkippableFact]
    public async Task TheForgotPasswordFormSubmits()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await client.GetStringAsync("/forgot-password");
        var fields = HiddenFields(html);
        fields.Add(new KeyValuePair<string, string>("_input.Email", "nobody-at-all@example.com"));

        using var response = await client.PostAsync("/forgot-password", new FormUrlEncodedContent(fields));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // And it reached the confirmation, rather than silently re-rendering the empty form —
        // which is what a rejected submission looks like to someone who cannot see the status.
        Assert.Contains("Check your email", body, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TheSignUpFormSubmits()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await client.GetStringAsync("/sign-up");
        var fields = HiddenFields(html);
        var email = $"form-test-{Guid.NewGuid():N}@example.com";
        fields.Add(new KeyValuePair<string, string>("_input.BusinessName", "Form Test Enterprise"));
        fields.Add(new KeyValuePair<string, string>("_input.FullName", "Form Tester"));
        fields.Add(new KeyValuePair<string, string>("_input.Email", email));
        fields.Add(new KeyValuePair<string, string>("_input.Password", "Correct-Horse-9-Battery"));

        using var response = await client.PostAsync("/sign-up", new FormUrlEncodedContent(fields));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Check your email", body, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ASubmissionWithoutATokenIsStillRejected()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // The other half of the fix. Removing the duplicate tag must not have removed the
        // protection: a post with no token at all still has to fail, or any site on the
        // internet could drive this form on a visitor's behalf.
        using var response = await client.PostAsync("/forgot-password", new FormUrlEncodedContent(
            new[]
            {
                new KeyValuePair<string, string>("_handler", "forgot-password"),
                new KeyValuePair<string, string>("_input.Email", "nobody@example.com")
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
