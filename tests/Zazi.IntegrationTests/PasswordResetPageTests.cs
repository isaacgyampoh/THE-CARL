extern alias portal;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Zazi.IntegrationTests.Postgres;

namespace Zazi.IntegrationTests;

/// <summary>
/// The password reset pages over real HTTP.
/// </summary>
/// <remarks>
/// Both are reachable only by people who cannot sign in, so anonymous access is not a
/// convenience here — a page that redirected to /sign-in would be unreachable by exactly the
/// people it exists for, and the redirect would look like ordinary traffic in the logs.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class PasswordResetPageTests
{
    private readonly PostgresFixture _postgres;

    public PasswordResetPageTests(PostgresFixture postgres) => _postgres = postgres;

    private sealed class PortalFactory : WebApplicationFactory<portal::Program>
    {
        private readonly string _connectionString;
        private readonly string? _publicBaseUrl;

        public PortalFactory(string connectionString, string? publicBaseUrl = "https://app.example.com")
        {
            _connectionString = connectionString;
            _publicBaseUrl = publicBaseUrl;
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Production");

            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zazi:DataProtectionKeyPath"] = Path.Combine(Path.GetTempPath(), "zazi-reset-keys"),
                    ["ConnectionStrings:DefaultConnection"] = _connectionString,
                    ["Zazi:BehindTlsProxy"] = "true",
                    ["Jwt:Key"] = "integration-test-signing-key-at-least-32-bytes-long",
                    ["Portal:PublicBaseUrl"] = _publicBaseUrl,
                    // The floor exists to hide timing from an attacker, not to slow a test suite.
                    ["PasswordReset:MinimumResponseMilliseconds"] = "0"
                }));

            return base.CreateHost(builder);
        }
    }

    private static HttpClient NonRedirectingClient(WebApplicationFactory<portal::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [SkippableTheory]
    [InlineData("/forgot-password")]
    [InlineData("/reset-password")]
    [InlineData("/reset-password?token=nonsense")]
    public async Task ResetPagesAreReachableWithoutSigningIn(string path)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);
        using var client = NonRedirectingClient(factory);

        using var response = await client.GetAsync(path);

        Assert.False(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"'{path}' redirected to '{response.Headers.Location}'. Nobody who needs this page can "
            + "sign in, so a redirect to /sign-in makes it unreachable.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task TheSignInPageOffersTheForgottenPasswordRoute()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);
        using var client = NonRedirectingClient(factory);

        var html = await client.GetStringAsync("/sign-in");

        // A reset page nobody can find is a support call.
        Assert.Contains("/forgot-password", html, StringComparison.Ordinal);
        Assert.Contains("Forgot password?", html, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TheForgotPasswordPageOffersAForm()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);
        using var client = NonRedirectingClient(factory);

        var html = await client.GetStringAsync("/forgot-password");

        Assert.Contains("Reset your password", html, StringComparison.Ordinal);

        // Without the antiforgery token in the markup, the post it protects is rejected and the
        // form silently never works.
        Assert.Contains("__RequestVerificationToken", html, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TheForgotPasswordPageSaysSoWhenNoPublicUrlIsConfigured()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!, publicBaseUrl: null);
        using var client = NonRedirectingClient(factory);

        var html = await client.GetStringAsync("/forgot-password");

        // Rendered rather than pretending to send, so somebody locked out is told to contact an
        // administrator instead of waiting for mail that cannot be sent.
        Assert.Contains("unavailable", html, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task AnUnusableResetLinkExplainsItselfRatherThanAskingForAPassword()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);
        using var client = NonRedirectingClient(factory);

        var html = await client.GetStringAsync("/reset-password?token=definitely-not-real");

        Assert.Contains("did not work", html, StringComparison.OrdinalIgnoreCase);

        // And it does not ask for a new password, which would only be refused after the person
        // had thought of one. Asserted on the form rather than on the heading text, because the
        // page title says "Choose a new password" in every branch.
        Assert.DoesNotContain("reset-password\" ", html, StringComparison.Ordinal);
        Assert.DoesNotContain("autocomplete=\"new-password\"", html, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TheDeploymentStartsWithoutAPublicUrlConfigured()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        // Adding this setting must not stop an existing deployment from starting. Password
        // reset refuses at the page instead, which is recoverable; a portal that will not boot
        // is not.
        using var factory = new PortalFactory(_postgres.ConnectionString!, publicBaseUrl: null);
        using var client = NonRedirectingClient(factory);

        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task ResetRequestsAreRateLimited()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);
        using var client = NonRedirectingClient(factory);

        // The existing per-IP authentication policy permits 10 a minute. This asserts the
        // attribute on the component actually reaches the endpoint as metadata — rate limiting
        // a Razor page is not the same mechanism as rate limiting a mapped endpoint, and
        // assuming it works would leave the one form anybody can reach unthrottled.
        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 14; attempt++)
        {
            using var response = await client.GetAsync("/forgot-password");
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);

        // And the limiter tells the caller when to come back, as the shared OnRejected handler
        // does for every other policy.
        using var rejected = await client.GetAsync("/forgot-password");
        if (rejected.StatusCode == HttpStatusCode.TooManyRequests)
        {
            Assert.True(
                rejected.Headers.Contains("Retry-After"),
                "A throttled response carried no Retry-After header.");
        }
    }
}
