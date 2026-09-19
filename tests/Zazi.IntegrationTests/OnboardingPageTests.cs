extern alias portal;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Zazi.IntegrationTests.Postgres;

namespace Zazi.IntegrationTests;

/// <summary>
/// That the two onboarding pages are reachable by people who cannot sign in.
/// </summary>
/// <remarks>
/// Both are useless if they are not anonymous, and both fail in ways that look like nothing is
/// wrong. A verification link that lands on a sign-in redirect cannot be followed by the one
/// person it was sent to — who, by definition, cannot sign in until they have followed it —
/// and the resulting log line is an ordinary redirect to /sign-in.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class OnboardingPageTests
{
    private readonly PostgresFixture _postgres;

    public OnboardingPageTests(PostgresFixture postgres) => _postgres = postgres;

    private sealed class PortalFactory : WebApplicationFactory<portal::Program>
    {
        private readonly string _connectionString;
        private readonly bool _signUpEnabled;

        public PortalFactory(string connectionString, bool signUpEnabled)
        {
            _connectionString = connectionString;
            _signUpEnabled = signUpEnabled;
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Production");

            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zazi:DataProtectionKeyPath"] =
                        Path.Combine(Path.GetTempPath(), "zazi-onboarding-keys"),
                    ["ConnectionStrings:DefaultConnection"] = _connectionString,
                    ["Zazi:BehindTlsProxy"] = "true",
                    ["Jwt:Key"] = "integration-test-signing-key-at-least-32-bytes-long",
                    ["SignUp:Enabled"] = _signUpEnabled ? "true" : "false",
                    // Portal, not SignUp: the public URL moved when password reset needed it
                    // too, since reset must work on deployments that never open signup.
                    ["Portal:PublicBaseUrl"] = "https://app.example.com"
                }));

            return base.CreateHost(builder);
        }
    }

    private static HttpClient NonRedirectingClient(WebApplicationFactory<portal::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [SkippableTheory]
    [InlineData("/sign-up")]
    [InlineData("/verify-email")]
    [InlineData("/verify-email?token=nonsense")]
    public async Task OnboardingPagesAreReachableWithoutSigningIn(string path)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!, signUpEnabled: true);
        using var client = NonRedirectingClient(factory);

        using var response = await client.GetAsync(path);

        Assert.False(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"'{path}' redirected to '{response.Headers.Location}'. Nobody who needs this page "
            + "is able to sign in, so a redirect to /sign-in makes it unreachable.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task TheSignUpPageOffersAFormWhenSignUpIsEnabled()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!, signUpEnabled: true);
        using var client = NonRedirectingClient(factory);

        var html = await client.GetStringAsync("/sign-up");

        Assert.Contains("Create your account", html, StringComparison.Ordinal);

        // The antiforgery token has to be in the markup, or the post it protects is rejected
        // and the form silently never works.
        Assert.Contains("__RequestVerificationToken", html, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TheSignUpPageSaysSoWhenTheDeploymentHasNotEnabledIt()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!, signUpEnabled: false);
        using var client = NonRedirectingClient(factory);

        var html = await client.GetStringAsync("/sign-up");

        // Rendered rather than 404, so someone following a link from marketing material finds
        // out why instead of concluding the site is broken.
        Assert.Contains("Accounts are not open", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Create your account", html, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TheSignInPageLinksToSignUp()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!, signUpEnabled: true);
        using var client = NonRedirectingClient(factory);

        var html = await client.GetStringAsync("/sign-in");

        // A signup page nobody can find is a signup page nobody uses.
        Assert.Contains("/sign-up", html, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AnUnusableVerificationLinkExplainsItselfRatherThanFailing()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!, signUpEnabled: true);
        using var client = NonRedirectingClient(factory);

        var html = await client.GetStringAsync("/verify-email?token=definitely-not-a-real-token");

        Assert.Contains("did not work", html, StringComparison.OrdinalIgnoreCase);
    }
}
