extern alias portal;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Zazi.IntegrationTests.Postgres;

namespace Zazi.IntegrationTests;

/// <summary>
/// That an error shows an error, instead of starting a redirect loop.
/// </summary>
/// <remarks>
/// <para>
/// The portal denies by default, and outside Development it re-executes failed requests at
/// <c>/error</c>. Put those two together with an error page that has not opted out of the
/// fallback policy and the result is not an error page — it is a loop. An anonymous visitor
/// triggers an exception, the handler re-executes at <c>/error</c>, the fallback policy sees an
/// unauthenticated user, and the cookie handler answers with a redirect to <c>/sign-in</c>.
/// Follow it and the same thing happens again.
/// </para>
/// <para>
/// Two properties make this specific failure expensive. It cannot happen in Development,
/// because the exception handler is only registered outside it — so it is invisible in every
/// local run. And it is indistinguishable in logs from the ordinary business of redirecting a
/// signed-out visitor to sign in, so nothing looks wrong while the whole dashboard is
/// unreachable.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class PortalErrorPageTests
{
    private readonly PostgresFixture _postgres;

    public PortalErrorPageTests(PostgresFixture postgres) => _postgres = postgres;

    private sealed class PortalFactory : WebApplicationFactory<portal::Program>
    {
        private readonly string _connectionString;

        public PortalFactory(string connectionString) => _connectionString = connectionString;

        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Production. The exception handler this test is about is not registered in
            // Development, so a Development run would prove nothing.
            builder.UseEnvironment("Production");

            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zazi:DataProtectionKeyPath"] =
                        Path.Combine(Path.GetTempPath(), "zazi-error-page-keys"),
                    ["ConnectionStrings:DefaultConnection"] = _connectionString,
                    ["Zazi:BehindTlsProxy"] = "true",
                    ["Jwt:Key"] = "integration-test-signing-key-at-least-32-bytes-long"
                }));

            return base.CreateHost(builder);
        }
    }

    private static HttpClient NonRedirectingClient(WebApplicationFactory<portal::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [SkippableFact]
    public async Task ThePathTheExceptionHandlerReExecutesAtIsAnonymouslyReachable()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);

        // Read the path out of the running application rather than restating it here. The two
        // halves of this bug are in different files — UseExceptionHandler in Program.cs, the
        // [AllowAnonymous] on the page — and a test that hard-codes the path stops connecting
        // them the moment someone changes one side.
        var configuredPath = factory.Services
            .GetRequiredService<IOptions<ExceptionHandlerOptions>>()
            .Value.ExceptionHandlingPath;

        Assert.True(
            configuredPath.HasValue,
            "No exception handling path is configured, so a failed request has nowhere to go.");

        using var client = NonRedirectingClient(factory);
        using var response = await client.GetAsync(configuredPath.Value!);

        // A 302 here is the first hop of a loop that never ends, because its target fails the
        // same way for the same reason. Anyone anonymous who hits any error is stuck.
        Assert.False(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"The exception handler re-executes at '{configuredPath}', and an anonymous request "
            + $"for it was redirected to '{response.Headers.Location}'. If that is /sign-in, then "
            + "every error an anonymous visitor hits becomes an infinite redirect loop.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task TheErrorPageIsReachableWithoutSigningIn()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);
        using var client = NonRedirectingClient(factory);

        // The exception handler re-executes at this path, so whether it is anonymous decides
        // whether an error is reportable or self-perpetuating.
        using var response = await client.GetAsync("/Error");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task TheErrorPageIsReachableByTheLowercasePathTheHandlerUses()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);
        using var client = NonRedirectingClient(factory);

        // UseExceptionHandler is configured with "/error" while the page declares "/Error".
        // Routing is case-insensitive so this works, but it works by grace rather than by
        // intent, and it is worth knowing if that ever stops being true.
        using var response = await client.GetAsync("/error");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task TheErrorPageDoesNotTellVisitorsToTurnOnDevelopmentMode()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);
        using var client = NonRedirectingClient(factory);

        using var response = await client.GetAsync("/Error");
        var html = await response.Content.ReadAsStringAsync();

        // Template boilerplate. Advising the operator of a financial system to switch on the
        // mode that prints exception detail to end users is advice worth not shipping.
        Assert.DoesNotContain("Development Mode", html, StringComparison.OrdinalIgnoreCase);
    }
}
