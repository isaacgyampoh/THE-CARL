using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Domain;
using Zazi.Infrastructure;
using Zazi.Infrastructure.Services;

// Creates the first organization, branch and owner of a Zazi deployment.
//
// Every API endpoint that could create these requires an authenticated caller, which leaves a
// fresh database with no way in. This closes that gap without opening an anonymous HTTP route
// that would have to be remembered and disabled afterwards: the tool needs database
// credentials, so the privilege it requires is the privilege it already takes to run.
//
// It refuses to touch a database that already has an organization. A bootstrap that also
// works on a live system is a way to add a tenant nobody authorised.

var arguments = ParseArguments(args);

if (arguments.ContainsKey("help") || args.Length == 0)
{
    PrintUsage();
    return 0;
}

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
    ?? Value(arguments, "connection");

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("No database configured. Set ConnectionStrings__DefaultConnection or pass --connection.");
    return 1;
}

var signingKey = Environment.GetEnvironmentVariable("ZAZI_JWT_KEY")
    ?? Environment.GetEnvironmentVariable("Jwt__Key");

if (string.IsNullOrWhiteSpace(signingKey))
{
    // AuthService issues tokens while registering a user, so it needs a key even though this
    // tool never hands one out.
    Console.Error.WriteLine("No signing key configured. Set ZAZI_JWT_KEY to the value the API uses.");
    return 1;
}

var organizationName = Value(arguments, "organization");
var branchName = Value(arguments, "branch") ?? "Main";
var ownerEmail = Value(arguments, "owner-email");
var ownerPassword = Value(arguments, "owner-password");
var ownerName = Value(arguments, "owner-name") ?? "Owner";

if (string.IsNullOrWhiteSpace(organizationName) ||
    string.IsNullOrWhiteSpace(ownerEmail) ||
    string.IsNullOrWhiteSpace(ownerPassword))
{
    Console.Error.WriteLine("--organization, --owner-email and --owner-password are required.");
    PrintUsage();
    return 1;
}

var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options;
await using var database = new ApplicationDbContext(options);

if (!await database.Database.CanConnectAsync())
{
    Console.Error.WriteLine("The database is not reachable. Check the connection string and that migrations have been applied.");
    return 1;
}

var pending = (await database.Database.GetPendingMigrationsAsync()).ToList();
if (pending.Count > 0)
{
    Console.Error.WriteLine(
        $"The database is missing {pending.Count} migration(s), starting with '{pending[0]}'. Apply them first.");
    return 1;
}

if (await database.Organizations.AnyAsync())
{
    Console.Error.WriteLine(
        "This database already has an organization. Bootstrap is for a new deployment only — " +
        "create further organizations, branches and staff through the API, where the action is " +
        "authenticated and audited.");
    return 1;
}

var organization = new Organization
{
    Name = organizationName,
    Country = Value(arguments, "country") ?? "GH",
    CurrencyCode = Value(arguments, "currency") ?? Money.DefaultCurrency
};
database.Organizations.Add(organization);

var branch = new Branch
{
    OrganizationId = organization.Id,
    Name = branchName,
    Location = Value(arguments, "location")
};
database.Branches.Add(branch);

await database.SaveChangesAsync();

var jwt = Options.Create(new JwtOptions
{
    Issuer = Environment.GetEnvironmentVariable("Jwt__Issuer") ?? "zazi",
    Audience = Environment.GetEnvironmentVariable("Jwt__Audience") ?? "zazi-clients",
    Key = signingKey
});

// The real registration path: the same password rules, the same PBKDF2 parameters and the
// same security stamp the API would produce. A bootstrap that hashed passwords its own way
// would create an account the API could not verify.
var auth = new AuthService(database, jwt, NullLogger<AuthService>.Instance);

var owner = await auth.RegisterUserAsync(new RegisterUserRequest(
    OrganizationId: organization.Id,
    BranchId: branch.Id,
    FullName: ownerName,
    Email: ownerEmail,
    Password: ownerPassword,
    PhoneNumber: Value(arguments, "owner-phone"),
    Roles: [ZaziRoles.Owner],
    EmailVerified: true,
    PhoneVerified: false));

Console.WriteLine($"organization  {organization.Id}  {organization.Name}");
Console.WriteLine($"branch        {branch.Id}  {branch.Name}");
Console.WriteLine($"owner         {owner.Id}  {ownerEmail}");

var agentEmail = Value(arguments, "agent-email");
var agentPassword = Value(arguments, "agent-password");

if (!string.IsNullOrWhiteSpace(agentEmail) && !string.IsNullOrWhiteSpace(agentPassword))
{
    var agent = await auth.RegisterUserAsync(new RegisterUserRequest(
        OrganizationId: organization.Id,
        BranchId: branch.Id,
        FullName: Value(arguments, "agent-name") ?? "Agent",
        Email: agentEmail,
        Password: agentPassword,
        PhoneNumber: Value(arguments, "agent-phone"),
        Roles: [ZaziRoles.Agent],
        EmailVerified: true,
        PhoneVerified: false));

    Console.WriteLine($"agent         {agent.Id}  {agentEmail}");
}

Console.WriteLine();
Console.WriteLine("Sign in to the web dashboard as the owner. Issue a device enrolment code from");
Console.WriteLine("the API as a manager or owner, then enter it in the Android app.");

// Passwords were supplied on the command line and are not echoed back, so a terminal
// scrollback or shell history does not gain a second copy from this output.
return 0;

static Dictionary<string, string?> ParseArguments(string[] args)
{
    var parsed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = args[i][2..];
        var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[++i]
            : null;

        parsed[key] = value;
    }

    return parsed;
}

static string? Value(Dictionary<string, string?> arguments, string key) =>
    arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

static void PrintUsage()
{
    Console.WriteLine("""
        Creates the first organization, branch and owner of a Zazi deployment.

          ConnectionStrings__DefaultConnection=…  ZAZI_JWT_KEY=…  \
          dotnet run --project src/Zazi.Bootstrap -- \
            --organization "Kofi Mobile Money" \
            --branch "Accra Central" \
            --owner-email owner@example.test \
            --owner-password '…' \
            [--owner-name "Kofi Mensah"] \
            [--agent-email agent@example.test --agent-password '…'] \
            [--country GH] [--currency GHS] [--location "Accra"]

        Refuses to run against a database that already has an organization.
        """);
}
