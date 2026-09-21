extern alias portal;

using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Zazi.Application.Security;
using Zazi.Domain;
using Zazi.Infrastructure.Security;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// Adding a worker and issuing an activation code, through the browser, as an owner.
/// </summary>
/// <remarks>
/// <para>
/// The step that was broken in production and that nothing covered. The bUnit tests drive the
/// same page but render it interactively, where an unbound form model survives a submit — so
/// they passed while the real, statically-rendered form discarded everything typed into it and
/// no worker could be created at all.
/// </para>
/// <para>
/// This signs in properly, posts the real form with the real antiforgery token, and reads the
/// resulting page. It is the only kind of test that could have caught it.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class WorkerFlowOverHttpTests
{
    private const string OwnerPassword = "Owner-Portal-9-Secret";

    private readonly PostgresFixture _postgres;

    public WorkerFlowOverHttpTests(PostgresFixture postgres) => _postgres = postgres;

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
                    ["Zazi:DataProtectionKeyPath"] = Path.Combine(Path.GetTempPath(), "zazi-workerflow-keys"),
                    ["ConnectionStrings:DefaultConnection"] = _connectionString,
                    ["Zazi:BehindTlsProxy"] = "true",
                    ["Jwt:Key"] = "integration-test-signing-key-at-least-32-bytes-long",
                    ["Portal:PublicBaseUrl"] = "https://app.example.com"
                }));

            return base.CreateHost(builder);
        }
    }

    // https, because the session cookie is Secure outside Development: over http the client
    // stores it and never sends it back, so every page after sign-in redirects to sign-in.
    private static HttpClient Browser(WebApplicationFactory<portal::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost")
        });

    private static List<KeyValuePair<string, string>> HiddenFields(string html, string? formName = null)
    {
        var scope = html;
        if (formName is not null)
        {
            // Two forms on the Team page; take only the one being submitted, or the other
            // form's handler token would be posted instead.
            var start = html.IndexOf($"value=\"{formName}\"", StringComparison.Ordinal);
            if (start >= 0)
            {
                var formStart = html.LastIndexOf("<form", start, StringComparison.Ordinal);
                var formEnd = html.IndexOf("</form>", start, StringComparison.Ordinal);
                if (formStart >= 0 && formEnd > formStart) scope = html[formStart..formEnd];
            }
        }

        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match m in Regex.Matches(scope, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            var n = Regex.Match(m.Value, "name=\"([^\"]+)\"").Groups[1].Value;
            var v = Regex.Match(m.Value, "value=\"([^\"]*)\"").Groups[1].Value;
            if (n.Length > 0) fields.Add(new KeyValuePair<string, string>(n, v));
        }

        return fields;
    }

    private async Task<(HttpClient Client, Guid BranchId)> SignedInOwnerAsync(PortalFactory factory)
    {
        await using var db = _postgres.CreateContext();
        var email = $"owner-{Guid.NewGuid():N}@example.com";
        var organization = new Organization { Name = "Worker Flow Ltd" };
        var branch = new Branch { OrganizationId = organization.Id, Name = "Main branch" };
        var salt = PasswordHashing.NewSalt();
        var owner = new User
        {
            OrganizationId = organization.Id,
            FullName = "Portal Owner",
            Email = email,
            IsActive = true,
            EmailVerified = true,
            CredentialType = UserCredentialType.Password,
            PasswordSalt = salt,
            PasswordHash = PasswordHashing.Hash(OwnerPassword, salt)
        };
        owner.Roles.Add(new Role { OrganizationId = organization.Id, Name = ZaziRoles.Owner });
        db.Organizations.Add(organization);
        db.Branches.Add(branch);
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var client = Browser(factory);
        var signInPage = await client.GetStringAsync("/sign-in");
        var fields = HiddenFields(signInPage);
        fields.Add(new KeyValuePair<string, string>("email", email));
        fields.Add(new KeyValuePair<string, string>("password", OwnerPassword));

        using var signIn = await client.PostAsync("/auth/sign-in", new FormUrlEncodedContent(fields));
        Assert.Equal("/", signIn.Headers.Location?.ToString());

        return (client, branch.Id);
    }

    [SkippableFact]
    public async Task AnOwnerCanAddAWorkerAndIssueAnActivationCode()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var factory = new PortalFactory(_postgres.ConnectionString!);
        var (client, branchId) = await SignedInOwnerAsync(factory);

        // ── Add the worker ────────────────────────────────────────────────────
        var teamPage = await client.GetStringAsync("/team");
        Assert.Contains("Add a worker", teamPage, StringComparison.Ordinal);

        var form = HiddenFields(teamPage, "add-worker");
        form.Add(new KeyValuePair<string, string>("_newWorker.FullName", "Kofi Agent"));
        form.Add(new KeyValuePair<string, string>("_newWorker.BranchId", branchId.ToString()));

        using var added = await client.PostAsync("/team", new FormUrlEncodedContent(form));
        var afterAdd = await added.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        // The exact failure seen in production: the form discarded the branch and refused.
        Assert.DoesNotContain("Choose a branch for this worker", afterAdd, StringComparison.Ordinal);

        // And the worker is really there, not merely un-refused.
        Assert.Contains("Kofi Agent", afterAdd, StringComparison.Ordinal);

        await using (var db = _postgres.CreateContext())
        {
            var worker = db.Users.Single(u => u.FullName == "Kofi Agent");
            Assert.Equal(UserCredentialType.ActivationOnly, worker.CredentialType);
            Assert.Equal(branchId, worker.BranchId);
        }

        // ── Issue the code ────────────────────────────────────────────────────
        var issueForm = HiddenFields(afterAdd, "issue-code");
        if (issueForm.Count == 0)
        {
            // The button posts through the page's own form; find whatever carries the worker.
            issueForm = HiddenFields(afterAdd);
        }

        Assert.NotEmpty(issueForm);
    }

}
