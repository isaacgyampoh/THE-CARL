namespace Zazi.Web.Security;

public static class WebRateLimitPolicies
{
    /// <summary>Per-IP limiter for the sign-in endpoint, matching the API's credential policy.</summary>
    public const string Authentication = "web-auth";

    /// <summary>
    /// Per-IP limiter for forgot-password and reset-password, separate from sign-in.
    /// </summary>
    /// <remarks>
    /// These shared the sign-in bucket, and the page loads counted as well as the submissions.
    /// So an owner who had failed to sign in five times, then followed the advice to set a new
    /// password — two page loads, two submissions — had spent the whole minute's allowance, and
    /// the sign-in with their brand-new password was refused with a bare 429. The way out was
    /// metered against the way in.
    /// </remarks>
    public const string Recovery = "web-recovery";
}
