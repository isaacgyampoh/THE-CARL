namespace Zazi.IntegrationTests;

/// <summary>
/// Process-wide configuration the API entry point needs before the host is built.
/// </summary>
/// <remarks>
/// Program.cs reads configuration and validates the signing key <i>before</i>
/// <c>builder.Build()</c>, so <c>ConfigureAppConfiguration</c> callbacks run too late to
/// supply it. Environment variables are part of the default configuration sources and are in
/// place before the entry point runs, which makes them the only reliable seam.
/// <para>
/// Shared by every factory so a test host cannot come up with a different signing key from
/// the one <see cref="TestTokens"/> signs with.
/// </para>
/// </remarks>
internal static class TestHostEnvironment
{
    public const string SigningKey = "thecarl-integration-test-signing-key-at-least-32-bytes";
    public const string Issuer = "zazi";
    public const string Audience = "zazi-clients";

    private static readonly object Gate = new();
    private static bool _applied;

    public static void Apply()
    {
        lock (Gate)
        {
            if (_applied)
            {
                return;
            }

            Environment.SetEnvironmentVariable("Jwt__Key", SigningKey);
            Environment.SetEnvironmentVariable("Jwt__Issuer", Issuer);
            Environment.SetEnvironmentVariable("Jwt__Audience", Audience);

            // Blank so Program.cs selects the in-memory provider by default. Factories that
            // want PostgreSQL replace the DbContext registration in ConfigureServices.
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", string.Empty);

            _applied = true;
        }
    }
}
