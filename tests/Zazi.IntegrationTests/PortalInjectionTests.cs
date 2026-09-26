using System.Text.RegularExpressions;

namespace Zazi.IntegrationTests;

/// <summary>
/// Every service a portal page asks for must be registered in the portal.
/// </summary>
/// <remarks>
/// <para>
/// A Razor page that injects a service the host has never heard of compiles, deploys, and
/// then answers 500 the first time somebody opens it. That is exactly how the message-formats
/// page shipped: the service existed and was registered — in the API, which is a different
/// container. Nothing caught it until the live page failed.
/// </para>
/// <para>
/// Textual on purpose. Building the portal host inside a test drags in a database, a signing
/// key and a mail provider, and the question here is narrower than that: does the file that
/// wires the portal mention every application service its pages depend on.
/// </para>
/// </remarks>
public class PortalInjectionTests
{
    private static readonly string Root = FindRepositoryRoot();

    /// <summary>Supplied by the framework or the host, never by this application's wiring.</summary>
    private static readonly HashSet<string> Framework = new(StringComparer.Ordinal)
    {
        "AuthenticationStateProvider", "IAuthorizationService", "IConfiguration",
        "IHttpClientFactory", "ILogger", "IOptions", "IServiceScopeFactory",
        "NavigationManager", "HttpClient", "IJSRuntime"
    };

    [Fact]
    public void EveryServiceAPageInjectsIsWiredIntoThePortal()
    {
        var program = File.ReadAllText(Path.Combine(Root, "src", "Zazi.Web", "Program.cs"));
        var pages = Directory.GetFiles(
            Path.Combine(Root, "src", "Zazi.Web", "Components"), "*.razor", SearchOption.AllDirectories);

        // Guards a vacuous pass: a wrong path would find no pages and agree with everything.
        Assert.NotEmpty(pages);

        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var page in pages)
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(page), @"@inject\s+([A-Za-z0-9_.]+)"))
            {
                // The last segment, so "Zazi.Application.Closing.IDayCloseService" and a
                // bare "IDayCloseService" are the same question.
                var name = match.Groups[1].Value.Split('.')[^1];

                if (Framework.Contains(name) || program.Contains(name, StringComparison.Ordinal))
                {
                    continue;
                }

                missing.Add($"{name} (injected by {Path.GetFileName(page)})");
            }
        }

        Assert.True(
            missing.Count == 0,
            "These are injected by a portal page but never registered in Zazi.Web/Program.cs, " +
            "so the page answers 500 the first time it is opened:\n  " + string.Join("\n  ", missing));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zazi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Zazi.sln not found.");
    }
}
