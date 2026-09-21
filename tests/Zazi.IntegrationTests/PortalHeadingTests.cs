extern alias portal;

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Zazi.IntegrationTests.Postgres;

namespace Zazi.IntegrationTests;

/// <summary>
/// Every page says what it is for, in its heading.
/// </summary>
/// <remarks>
/// <para>
/// All six pages an unauthenticated visitor can reach — sign in, sign up, verify email, forgot
/// password, reset password, error — carried <c>&lt;h1&gt;Zazi&lt;/h1&gt;</c>, with what the
/// page actually did demoted to grey type beneath it. A heading is the one thing a screen
/// reader offers to jump between and the first thing anyone skims, so six different pages
/// announced themselves identically.
/// </para>
/// <para>
/// The product name belongs on these pages: someone who followed a link out of their email
/// needs to know where they have landed. It belongs in a masthead, not in the heading.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class PortalHeadingTests
{
    private readonly PostgresFixture _postgres;

    public PortalHeadingTests(PostgresFixture postgres) => _postgres = postgres;

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
                    ["Zazi:DataProtectionKeyPath"] =
                        Path.Combine(Path.GetTempPath(), "zazi-heading-keys"),
                    ["ConnectionStrings:DefaultConnection"] = _connectionString,
                    ["Zazi:BehindTlsProxy"] = "true",
                    // SignUpOptions.SectionName is "SignUp", not nested under Zazi. With the wrong key
                    // the page renders its "accounts are not open" branch instead.
                    ["SignUp:Enabled"] = "true",
                    // Required whenever signup is enabled — the application refuses to start
                    // otherwise, because verification links are built from it and a signup
                    // that mails a broken link creates accounts nobody can open.
                    ["Portal:PublicBaseUrl"] = "https://localhost",
                    ["Jwt:Key"] = "integration-test-signing-key-at-least-32-bytes-long"
                }));

            return base.CreateHost(builder);
        }
    }

    public static TheoryData<string, string> AnonymousPages() => new()
    {
        { "/sign-in", "Sign in" },
        { "/sign-up", "Create" },
        { "/forgot-password", "Reset" },
    };

    [SkippableTheory]
    [MemberData(nameof(AnonymousPages))]
    public async Task APagesHeadingSaysWhatThePageIsFor(string path, string expectedInHeading)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await client.GetStringAsync(path);
        var headings = HeadingsIn(html);

        Assert.NotEmpty(headings);

        // The product name is not a description of the page. Six pages that all say "Zazi"
        // tell a screen reader user nothing about which one they are on.
        Assert.DoesNotContain(
            headings,
            heading => heading.Equals("Zazi", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            headings,
            heading => heading.Contains(expectedInHeading, StringComparison.OrdinalIgnoreCase));
    }

    [SkippableTheory]
    [InlineData("/sign-in")]
    [InlineData("/sign-up")]
    [InlineData("/forgot-password")]
    public async Task ThePageStillSaysWhoseSoftwareThisIs(string path)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await client.GetStringAsync(path);

        // Moving the name out of the heading must not remove it. Someone who followed a link
        // from an email and cannot tell whose site this is will not type a password into it.
        Assert.Contains("masthead", html, StringComparison.Ordinal);
        Assert.Contains("Zazi", html, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("/sign-in")]
    [InlineData("/sign-up")]
    public async Task ExactlyOneHeadingIsShownAtATime(string path)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await client.GetStringAsync(path);

        // These pages branch: sign-up alone has three states, each with its own heading. Only
        // one may render, or the document has several competing h1s and the outline is
        // meaningless.
        Assert.Single(HeadingsIn(html));
    }

    private static List<string> HeadingsIn(string html) =>
        Regex.Matches(html, @"<h1[^>]*>(?<text>.*?)</h1>", RegexOptions.Singleline)
            .Select(match => Regex.Replace(match.Groups["text"].Value, "<.*?>", string.Empty).Trim())
            .Where(text => text.Length > 0)
            .ToList();
}
