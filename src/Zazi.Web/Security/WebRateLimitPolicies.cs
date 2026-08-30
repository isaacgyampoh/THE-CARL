namespace Zazi.Web.Security;

public static class WebRateLimitPolicies
{
    /// <summary>Per-IP limiter for the sign-in endpoint, matching the API's credential policy.</summary>
    public const string Authentication = "web-auth";
}
