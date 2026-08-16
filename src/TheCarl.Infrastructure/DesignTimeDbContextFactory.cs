using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TheCarl.Infrastructure;

/// <summary>
/// Supplies a PostgreSQL-configured context to the EF Core CLI.
/// </summary>
/// <remarks>
/// Without this, the tooling boots the API host, which selects the in-memory provider when
/// no connection string is configured. Scaffolding then fails with "Unable to resolve
/// service for type 'IMigrator'", because the in-memory provider has no migrator.
/// <para>
/// The connection string here is used only to pick the provider and generate SQL; no
/// database is contacted for <c>migrations add</c>, <c>remove</c>, or <c>script</c>. Set
/// <c>ConnectionStrings__DefaultConnection</c> to target a real database for
/// <c>database update</c>.
/// </para>
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    private const string ScaffoldingPlaceholder =
        "Host=localhost;Port=5432;Database=thecarl_design;Username=postgres";

    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection") is { Length: > 0 } configured
                ? configured
                : ScaffoldingPlaceholder;

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ApplicationDbContext(options);
    }
}
