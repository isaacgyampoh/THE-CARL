using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

namespace Zazi.Web.Security;

/// <summary>
/// Refuses to start if a page that has to be reachable without signing in is not.
/// </summary>
/// <remarks>
/// <para>
/// The portal denies by default. Two pages must opt out, and if either fails to, the result is
/// not a broken page — it is a redirect loop that makes the entire dashboard unreachable:
/// </para>
/// <list type="bullet">
/// <item><c>/sign-in</c>, because it is where unauthenticated visitors are sent. If it is not
/// anonymous it redirects to itself.</item>
/// <item>the exception handler's path, because failed requests are re-executed there. If it is
/// not anonymous, any error hit by an anonymous visitor redirects to <c>/sign-in</c>, which
/// fails the same way, forever.</item>
/// </list>
/// <para>
/// Both failures are invisible until they are in production. <c>[AllowAnonymous]</c> is a
/// component annotation that has to survive being turned into endpoint metadata, and the
/// exception handler is not even registered in Development. Worse, while either is happening
/// the logs look entirely normal: a stream of redirects to the sign-in page is what a healthy
/// portal serving signed-out visitors looks like.
/// </para>
/// <para>
/// So this checks the endpoints the application actually built, not the source. A startup
/// failure naming the page is recoverable in minutes. The alternative is a dashboard that
/// appears to be running while nobody can get into it.
/// </para>
/// </remarks>
public static class AnonymousRouteGuard
{
    /// <summary>
    /// Verifies that every path in <paramref name="requiredAnonymousPaths"/> maps to an endpoint
    /// carrying <see cref="IAllowAnonymous"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// If a path has no endpoint, or has one that would be caught by the fallback policy.
    /// </exception>
    public static void AssertAnonymouslyReachable(
        this IEndpointRouteBuilder endpoints,
        params string[] requiredAnonymousPaths)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Materialised once: the composite data source enumerates every registered source,
        // including the one Razor components add, and that is the point — the check has to see
        // what routing sees.
        var routeEndpoints = endpoints.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        var problems = new List<string>();

        foreach (var path in requiredAnonymousPaths)
        {
            var normalized = path.TrimStart('/');

            var matches = routeEndpoints
                .Where(endpoint => string.Equals(
                    endpoint.RoutePattern.RawText?.TrimStart('/'),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                problems.Add(
                    $"  {path} — no endpoint is registered for this path. Unmatched paths fall to "
                    + "the authorization fallback policy, so requests for it are redirected to "
                    + "/sign-in.");
                continue;
            }

            // Every endpoint on the path, not just one: a path served by several endpoints is
            // only anonymous if the one that wins is, and which wins is not worth guessing.
            foreach (var endpoint in matches.Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null))
            {
                problems.Add(
                    $"  {path} — endpoint '{endpoint.DisplayName}' does not allow anonymous access. "
                    + "Add [AllowAnonymous] to the page, or .AllowAnonymous() to the mapping.");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Refusing to start: a page that must be reachable without signing in is not.\n\n"
                + string.Join("\n", problems)
                + "\n\nLeft running, this is not a broken page. Unauthenticated visitors are "
                + "redirected to a page that redirects them again, so nobody can sign in and "
                + "nothing is reachable — while the logs show ordinary redirects to /sign-in.");
        }
    }
}
