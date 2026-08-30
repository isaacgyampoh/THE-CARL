using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zazi.Infrastructure;

namespace Zazi.IntegrationTests;

/// <summary>
/// Boots the real API pipeline — real authentication handler, real authorization policies,
/// real middleware — against an isolated in-memory store.
/// </summary>
/// <remarks>
/// Authorization is a property of the composed pipeline, not of any single class. Testing it
/// by calling controller methods directly would bypass the very filters under test, so these
/// tests go over HTTP through <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// </remarks>
public sealed class ZaziApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Signing key the host is configured with; tests mint tokens with the same key.</summary>
    public const string SigningKey = TestHostEnvironment.SigningKey;

    public const string Issuer = TestHostEnvironment.Issuer;
    public const string Audience = TestHostEnvironment.Audience;

    private readonly string _databaseName = $"carl-tests-{Guid.NewGuid():N}";

    static ZaziApiFactory() => TestHostEnvironment.Apply();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Each factory gets its own store so tests cannot observe one another's tenants.
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }

    /// <summary>Runs work against the API's own database scope.</summary>
    public async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await action(db);
    }

    public async Task WithDbAsync(Func<ApplicationDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await action(db);
    }
}
