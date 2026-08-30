namespace Zazi.Api.Security;

public static class RateLimitPolicies
{
    /// <summary>Per-IP limiter for credential endpoints (login, refresh, registration).</summary>
    public const string Authentication = "auth";

    /// <summary>Per-user limiter for authenticated tenant traffic.</summary>
    public const string Tenant = "tenant";
}
