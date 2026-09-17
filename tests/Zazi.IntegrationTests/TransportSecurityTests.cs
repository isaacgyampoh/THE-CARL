using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Zazi.IntegrationTests;

/// <summary>
/// Guards the transport rule in <c>Program.cs</c>: outside Development, Zazi refuses to serve
/// plain HTTP unless TLS is declared to terminate at a proxy in front of it.
/// </summary>
/// <remarks>
/// The rule exempts an in-memory test host, which has no socket and therefore no cleartext to
/// expose. That exemption is written as "everything except a TestHost server is a real
/// listener", so it fails closed. An earlier version asked the opposite question — whether the
/// server was Kestrel — and silently protected nothing: ASP.NET registers the internal
/// <c>KestrelServerImpl</c>, not the public <c>KestrelServer</c>, so the test was never true
/// and a Production host happily served credentials over plain HTTP.
///
/// These tests pin the one fact that exemption depends on. If a future ASP.NET release moves
/// the test server out of that namespace, the test host starts failing the transport check and
/// these assertions say why, instead of the real guard quietly changing behaviour.
/// </remarks>
public sealed class TransportSecurityTests : IClassFixture<ZaziApiFactory>
{
    private readonly ZaziApiFactory _factory;

    public TransportSecurityTests(ZaziApiFactory factory) => _factory = factory;

    [Fact]
    public void TheTestHostIsExemptOnlyBecauseItIsNotARealListener()
    {
        // Forces the host to build, which is where the transport guard runs. Reaching this
        // line at all means the guard did not reject the test host.
        var server = _factory.Services.GetRequiredService<IServer>();

        Assert.StartsWith(
            "Microsoft.AspNetCore.TestHost.",
            server.GetType().FullName,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheHostUnderTestIsNotDevelopment()
    {
        // The guard skips Development wholesale. If the test host ran as Development these
        // tests would prove nothing about the exemption, because the rule would never be
        // reached in the first place.
        var environment = _factory.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();

        Assert.NotEqual("Development", environment.EnvironmentName);
    }
}
