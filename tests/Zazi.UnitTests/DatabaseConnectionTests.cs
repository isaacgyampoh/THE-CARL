using Zazi.Infrastructure;

namespace Zazi.UnitTests;

/// <summary>
/// The pool ceiling each service applies to its database connection string.
/// </summary>
/// <remarks>
/// Npgsql's default of 100 per process is larger than the whole connection budget of the small
/// managed instance Zazi runs on. Two services filling their default pools would exhaust it,
/// and a database refusing connections fails every request at once.
/// </remarks>
public class DatabaseConnectionTests
{
    private const string Base = "Host=db.example;Database=zazi;Username=zazi;Password=secret";

    [Fact]
    public void ACeilingIsAppliedWhenTheDeploymentHasNotSetOne()
    {
        var result = DatabaseConnection.WithPoolCeiling(Base, maximumPoolSize: 20);

        Assert.Contains("Maximum Pool Size=20", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AConfiguredCeilingIsLeftAlone()
    {
        // An operator who has sized the pool for a larger database must not have it quietly
        // reduced by a default that knew nothing about their instance.
        var configured = Base + ";Maximum Pool Size=50";

        var result = DatabaseConnection.WithPoolCeiling(configured, maximumPoolSize: 20);

        Assert.Contains("Maximum Pool Size=50", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Maximum Pool Size=20", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WaitingForAConnectionIsBounded()
    {
        var result = DatabaseConnection.WithPoolCeiling(Base, maximumPoolSize: 20);

        // A request that cannot get a connection should fail with a clear error rather than
        // hang until the client retries and adds more load.
        Assert.Contains("Timeout=15", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRestOfTheConnectionStringSurvives()
    {
        var result = DatabaseConnection.WithPoolCeiling(Base + ";SSL Mode=Require", maximumPoolSize: 20);

        Assert.Contains("Host=db.example", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Database=zazi", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SSL Mode=Require", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyConnectionStringIsRefused()
    {
        Assert.Throws<ArgumentException>(() => DatabaseConnection.WithPoolCeiling("  ", 20));
    }
}
