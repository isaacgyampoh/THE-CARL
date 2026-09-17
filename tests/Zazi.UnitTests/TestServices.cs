using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zazi.Application.Security;
using Zazi.Infrastructure;
using Zazi.Infrastructure.Services;

namespace Zazi.UnitTests;

/// <summary>Shared construction helpers so tests build services the way production does.</summary>
internal static class TestServices
{
    /// <summary>A signing key long enough to satisfy <see cref="JwtOptions.Validate"/>.</summary>
    public const string TestSigningKey = "zazi-unit-test-signing-key-at-least-32-bytes-long";

    public static JwtOptions CreateJwtOptions() => new()
    {
        Key = TestSigningKey,
        Issuer = "zazi",
        Audience = "zazi-clients",
        AccessTokenMinutes = 60,
        RefreshTokenDays = 14
    };

    public static AuthService CreateAuthService(ApplicationDbContext db) =>
        new(db, Options.Create(CreateJwtOptions()), NullLogger<AuthService>.Instance);

    public static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ApplicationDbContext(options);
    }
}
