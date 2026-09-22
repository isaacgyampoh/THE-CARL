extern alias portal;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Domain;
using Zazi.Infrastructure;
using Zazi.IntegrationTests.Postgres;

namespace Zazi.IntegrationTests;

/// <summary>
/// That the sign-in page is reachable without signing in.
/// </summary>
/// <remarks>
/// <para>
/// The portal denies by default: <c>AddAuthorization</c> sets a <c>FallbackPolicy</c>
/// requiring an authenticated user, so a page is protected unless it opts out. Exactly one
/// page must opt out, and it is the one people arrive at — <c>SignIn.razor</c> carries
/// <c>[AllowAnonymous]</c>.
/// </para>
/// <para>
/// If that opt-out ever stops taking effect the failure is total and self-inflicted:
/// unauthenticated users are redirected to <c>/sign-in</c>, which redirects them to
/// <c>/sign-in</c>, forever. Nobody can sign in, so nobody can reach anything. It is not a
/// degraded state, it is the whole dashboard gone, and the redirect target looks correct in
/// logs while it happens.
/// </para>
/// <para>
/// Nothing enforced this. The attribute is a component annotation that has to survive being
/// turned into endpoint metadata, and <c>Routes.razor</c> uses a plain <c>RouteView</c>
/// rather than <c>AuthorizeRouteView</c>, so there is no component-level check as a second
/// line. These tests are that line.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class PortalSignInRoutingTests
{
    private const string SignInPath = "/sign-in";

    private readonly PostgresFixture _postgres;

    public PortalSignInRoutingTests(PostgresFixture postgres) => _postgres = postgres;

    private sealed class PortalFactory : WebApplicationFactory<portal::Program>
    {
        private readonly string _connectionString;

        public PortalFactory(string connectionString) => _connectionString = connectionString;

        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Production, not Development. The reported failure was on a production host, and
            // the portal takes different paths for cookie security and HTTPS depending on
            // environment — testing the one that is not deployed proves the wrong thing.
            builder.UseEnvironment("Production");

            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zazi:DataProtectionKeyPath"] =
                        Path.Combine(Path.GetTempPath(), "zazi-signin-routing-keys"),
                    ["ConnectionStrings:DefaultConnection"] = _connectionString,
                    // TLS is terminated by Caddy in production; without this the portal
                    // refuses to start rather than serving cleartext unknowingly.
                    ["Zazi:BehindTlsProxy"] = "true",
                    // Jwt:Key, not ZAZI_JWT_KEY: the environment variable is only consulted
                    // as a fallback when the configuration value is absent, and in-memory
                    // configuration is not the environment.
                    ["Jwt:Key"] = "integration-test-signing-key-at-least-32-bytes-long"
                }));

            return base.CreateHost(builder);
        }
    }

    /// <summary>A client that reports redirects instead of following them.</summary>
    private static HttpClient NonRedirectingClient(WebApplicationFactory<portal::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    // ─── The framework script ────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheFrameworkScriptLoadsWithoutSigningIn()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var factory = new PortalFactory(_postgres.ConnectionString!);

        // Served as a routed endpoint in .NET 8, so deny-by-default caught it: the sign-in page
        // loaded it, got a redirect to sign in instead, and threw a script error in production.
        var response = await NonRedirectingClient(factory).GetAsync("/_framework/blazor.web.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("javascript", response.Content.Headers.ContentType?.MediaType ?? "", StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("/")]
    [InlineData("/float")]
    [InlineData("/_framework/not-the-script.js")]
    public async Task OpeningTheScriptOpensNothingElse(string path)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var factory = new PortalFactory(_postgres.ConnectionString!);

        var response = await NonRedirectingClient(factory).GetAsync(path);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // ─── The sign-in page itself ─────────────────────────────────────────────

    [SkippableFact]
    public async Task TheSignInPageIsReachableWithoutSigningIn()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var factory = new PortalFactory(_postgres.ConnectionString!);

        var response = await NonRedirectingClient(factory).GetAsync(SignInPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sign in", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task TheSignInPageNeverRedirectsToItself()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var factory = new PortalFactory(_postgres.ConnectionString!);

        var response = await NonRedirectingClient(factory).GetAsync(SignInPath);

        // Stated separately from the test above, because a redirect to somewhere else would
        // be a different bug with a different cause, and this one is the loop.
        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Found, response.StatusCode);

        var location = response.Headers.Location?.ToString() ?? string.Empty;
        Assert.DoesNotContain(SignInPath, location, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task FollowingTheRedirectFromTheRootTerminates()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var factory = new PortalFactory(_postgres.ConnectionString!);

        // What a person actually does: open the dashboard and let the browser follow. A loop
        // shows up here as an exhausted redirect limit rather than a page.
        var response = await factory
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true })
            .GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sign in", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // ─── Protection is still in place ────────────────────────────────────────

    [SkippableTheory]
    [InlineData("/")]
    [InlineData("/team")]
    [InlineData("/operations")]
    [InlineData("/held")]
    public async Task ProtectedPagesRedirectToSignInWithAReturnUrl(string path)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var factory = new PortalFactory(_postgres.ConnectionString!);

        var response = await NonRedirectingClient(factory).GetAsync(path);

        // Deny-by-default has to keep working. A fix that made sign-in reachable by
        // weakening the fallback policy would pass the tests above and fail these.
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        var location = response.Headers.Location?.ToString() ?? string.Empty;
        Assert.Contains(SignInPath, location, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ReturnUrl", location, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Uri.EscapeDataString(path), location, StringComparison.OrdinalIgnoreCase);
    }

    // ─── And signing in still gets you in ────────────────────────────────────

    [SkippableFact]
    public async Task AnAuthenticatedOwnerReachesAProtectedPage()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var factory = new PortalFactory(_postgres.ConnectionString!);

        const string password = "Sup3rSecret!Passw0rd";
        var email = await SeedOwnerAsync(factory, password);

        var client = NonRedirectingClient(factory);

        // The real sign-in endpoint with the real antiforgery token, not a synthetic
        // principal: a fake scheme would prove the page renders and nothing about whether
        // anyone can actually get to it.
        var page = await client.GetAsync(SignInPath);
        var token = ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());

        var signIn = await client.PostAsync("/auth/sign-in", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("email", email),
            new KeyValuePair<string, string>("password", password),
            new KeyValuePair<string, string>("__RequestVerificationToken", token)
        }));

        Assert.Equal(HttpStatusCode.Found, signIn.StatusCode);
        Assert.DoesNotContain(SignInPath, signIn.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        // The session cookie is carried by hand rather than by the client's cookie jar.
        //
        // In Production the cookie is marked Secure, and the in-memory test server speaks
        // plain HTTP, so a browser-like client would correctly refuse to send it back and
        // this test would fail for a reason that has nothing to do with authorization. In
        // real production Caddy terminates TLS and the request is genuinely secure.
        //
        // Weakening the cookie policy to make this convenient would be testing a portal
        // nobody deploys, so the transport is worked around and the policy left alone.
        var cookie = signIn.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith("zazi.web=", StringComparison.Ordinal))
            : null;

        Assert.NotNull(cookie);

        var authenticated = new HttpRequestMessage(HttpMethod.Get, "/");
        authenticated.Headers.Add("Cookie", cookie!.Split(';')[0]);

        var dashboard = await client.SendAsync(authenticated);
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");

        Assert.True(match.Success, "The sign-in page carried no antiforgery token.");
        return match.Groups[1].Value;
    }

    private static async Task<string> SeedOwnerAsync(
        WebApplicationFactory<portal::Program> factory, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();

        var organization = new Organization { Name = $"Tenant {Guid.NewGuid():N}", Country = "GH", CurrencyCode = "GHS" };
        db.Organizations.Add(organization);
        db.Branches.Add(new Branch { OrganizationId = organization.Id, Name = "Accra Central" });
        await db.SaveChangesAsync();

        var email = $"owner-{Guid.NewGuid():N}@example.com";
        await scope.ServiceProvider.GetRequiredService<IAuthService>().RegisterUserAsync(
            new RegisterUserRequest(organization.Id, null, "Kwame Mensah", email, password, null,
                new[] { ZaziRoles.Owner }));

        return email;
    }
}
