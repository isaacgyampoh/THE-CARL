extern alias portal;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

using Guard = portal::Zazi.Web.Security.AnonymousRouteGuard;

namespace Zazi.IntegrationTests;

/// <summary>
/// The startup guard that keeps the portal from booting into a redirect loop.
/// </summary>
/// <remarks>
/// Tested against a synthetic endpoint set rather than by breaking a real page, so the guard's
/// own logic stays covered instead of depending on someone remembering to try it. Invoked
/// statically because the extension syntax would need the portal namespace imported, and
/// importing it wholesale into this project is more entanglement than one method is worth.
/// </remarks>
public class AnonymousRouteGuardTests
{
    private static RouteEndpoint Endpoint(string pattern, bool anonymous) =>
        new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(pattern),
            order: 0,
            new EndpointMetadataCollection(
                anonymous ? new object[] { new AllowAnonymousAttribute() } : Array.Empty<object>()),
            displayName: pattern);

    private static IEndpointRouteBuilder BuilderWith(params Endpoint[] endpoints)
    {
        var builder = new StubEndpointRouteBuilder();
        builder.DataSources.Add(new StubDataSource(endpoints));
        return builder;
    }

    [Fact]
    public void APageThatAllowsAnonymousAccessPasses() =>
        Guard.AssertAnonymouslyReachable(
            BuilderWith(Endpoint("/sign-in", anonymous: true)),
            "/sign-in");

    [Fact]
    public void APageThatLostItsAttributeIsRefusedByName()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Guard.AssertAnonymouslyReachable(
                BuilderWith(Endpoint("/sign-in", anonymous: false)),
                "/sign-in"));

        Assert.Contains("/sign-in", error.Message, StringComparison.Ordinal);
        Assert.Contains("AllowAnonymous", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APathWithNoEndpointAtAllIsRefused()
    {
        // The failure behind the original production report: an unmatched path does not 404,
        // it falls to the authorization fallback policy and redirects to /sign-in like
        // everything else — which is indistinguishable from working correctly.
        var error = Assert.Throws<InvalidOperationException>(() =>
            Guard.AssertAnonymouslyReachable(
                BuilderWith(Endpoint("/somewhere-else", anonymous: true)),
                "/sign-in"));

        Assert.Contains("no endpoint is registered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchingIgnoresCaseTheWayRoutingDoes() =>
        // UseExceptionHandler is configured with "/error" while the page declared "/Error" for
        // most of this project's life. Routing does not care, so neither may this.
        Guard.AssertAnonymouslyReachable(
            BuilderWith(Endpoint("/Error", anonymous: true)),
            "/error");

    [Fact]
    public void EveryEndpointOnAPathMustAllowAnonymousAccess()
    {
        // One anonymous endpoint is not enough if another on the same path is not: which of
        // them wins a given request is not something this check should try to predict.
        var error = Assert.Throws<InvalidOperationException>(() =>
            Guard.AssertAnonymouslyReachable(
                BuilderWith(
                    Endpoint("/sign-in", anonymous: true),
                    Endpoint("/sign-in", anonymous: false)),
                "/sign-in"));

        Assert.Contains("does not allow anonymous access", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFailingPathIsReportedTogether()
    {
        // One restart per problem is a poor way to discover there were two.
        var error = Assert.Throws<InvalidOperationException>(() =>
            Guard.AssertAnonymouslyReachable(
                BuilderWith(
                    Endpoint("/sign-in", anonymous: false),
                    Endpoint("/error", anonymous: false)),
                "/sign-in", "/error"));

        Assert.Contains("/sign-in", error.Message, StringComparison.Ordinal);
        Assert.Contains("/error", error.Message, StringComparison.Ordinal);
    }

    private sealed class StubDataSource : EndpointDataSource
    {
        public StubDataSource(IReadOnlyList<Endpoint> endpoints) => Endpoints = endpoints;

        public override IReadOnlyList<Endpoint> Endpoints { get; }

        public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
    }

    private sealed class StubEndpointRouteBuilder : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();

        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }
}
