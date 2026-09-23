using Npgsql;

namespace Zazi.Infrastructure;

/// <summary>
/// The connection string each service actually uses.
/// </summary>
/// <remarks>
/// <para>
/// Npgsql pools up to 100 connections per process by default. Zazi's production database is a
/// small managed instance whose connection limit is far below the total two services would
/// take if both filled their pools, and a database that refuses connections fails every
/// request at once — including the ones already in flight.
/// </para>
/// <para>
/// So each service states its own ceiling, well inside the server's, unless the deployment
/// has set one explicitly. A pool that is smaller than the burst makes a request wait for a
/// free connection; a pool that is larger than the server allows makes it fail outright.
/// Waiting is the better failure.
/// </para>
/// </remarks>
public static class DatabaseConnection
{
    /// <summary>
    /// Applies a pool ceiling and a connection timeout when the deployment has not set them.
    /// </summary>
    /// <param name="connectionString">The configured connection string.</param>
    /// <param name="maximumPoolSize">
    /// The most connections this process may hold. The API gets the larger share because it
    /// serves the handsets; the portal serves people, who are far fewer.
    /// </param>
    public static string WithPoolCeiling(string connectionString, int maximumPoolSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        // Explicit configuration always wins: an operator who has sized the pool for a bigger
        // database should not have it quietly reduced by a default.
        if (!connectionString.Contains("Maximum Pool Size", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("MaxPoolSize", StringComparison.OrdinalIgnoreCase))
        {
            builder.MaxPoolSize = maximumPoolSize;
        }

        if (!connectionString.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            // Waiting for a free connection is fine; waiting forever is not. A request that
            // cannot get one inside this window fails with a clear error rather than hanging
            // until the client gives up and retries, which only adds load.
            builder.Timeout = 15;
        }

        return builder.ConnectionString;
    }
}
