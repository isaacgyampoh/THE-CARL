extern alias portal;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Zazi.Application;
using Zazi.Application.Email;
using Zazi.Application.Onboarding;
using Zazi.Application.Security;

using Zazi.IntegrationTests.Postgres;

namespace Zazi.IntegrationTests;

/// <summary>
/// That the owner portal can start at all.
/// </summary>
/// <remarks>
/// <para>
/// This exists because it could not. <c>DeviceService</c> gained a dependency on
/// <c>IIdentityRevocationService</c>, which was registered in the API's container but not the
/// portal's, and the portal stopped booting — every request failing, with nothing in the test
/// suite to notice, because no test had ever built the portal's dependency graph.
/// </para>
/// <para>
/// A container that validates on build is the cheapest possible guard against a whole class
/// of outage: a service added in one host and forgotten in the other.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class PortalStartupTests
{
    private readonly PostgresFixture _postgres;

    public PortalStartupTests(PostgresFixture postgres) => _postgres = postgres;

    private sealed class PortalFactory : WebApplicationFactory<portal::Program>
    {
        private readonly string _connectionString;

        public PortalFactory(string connectionString) => _connectionString = connectionString;

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            // Data protection needs somewhere to put keys, and the portal refuses to start
            // without it rather than silently keeping them in memory where a restart would
            // sign every user out.
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zazi:DataProtectionKeyPath"] = Path.Combine(Path.GetTempPath(), "zazi-portal-test-keys"),
                    // The portal refuses to start without a real database rather than falling
                    // back to an in-memory one, which is correct and is why this test needs a
                    // server rather than being a pure container check.
                    ["ConnectionStrings:DefaultConnection"] = _connectionString
                }));

            return base.CreateHost(builder);
        }
    }

    [SkippableFact]
    public void ThePortalsContainerResolvesEveryServiceItsPagesNeed()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);

        // Forces the host to build, which validates every registered descriptor.
        using var scope = factory.Services.CreateScope();

        // The services the Team page injects. Named individually so a failure says which one
        // is missing rather than only that the portal is broken.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAuthService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDeviceService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDeviceEnrollmentService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IOrganizationService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IIdentityRevocationService>());

        // Signup and email verification are the portal's job, so the portal is the host that
        // must be able to send. Registered for both hosts from one extension method precisely
        // to avoid repeating the omission this test was written for.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IEmailSender>());

        // Self-service signup. Registered only in the portal, since the API has no signup
        // surface — which is exactly the shape of omission this test exists for.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISignUpService>());

        // Password reset. Portal-only like signup, and the page that needs it is reached
        // by people who are already locked out — the worst audience for a 500.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPasswordResetService>());
    }
}
