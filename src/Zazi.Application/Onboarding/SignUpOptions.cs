using Zazi.Application.Security;

namespace Zazi.Application.Onboarding;

/// <summary>Settings for self-service onboarding.</summary>
public sealed class SignUpOptions
{
    public const string SectionName = "SignUp";

    /// <summary>
    /// Whether strangers may create accounts.
    /// </summary>
    /// <remarks>
    /// Off by default. Opening a financial system to public registration is a decision a
    /// deployment makes on purpose, not one it inherits from a default — a pilot running with
    /// a handful of known agents has no reason to accept accounts from anyone who finds the URL.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>How long a verification link stays valid.</summary>
    public int VerificationValidForHours { get; set; } = 24;

    /// <summary>The shortest gap between verification emails to the same account.</summary>
    public int ResendCooldownMinutes { get; set; } = 2;

    /// <summary>The name given to the branch created alongside a new organization.</summary>
    public string FirstBranchName { get; set; } = "Main branch";

    /// <summary>
    /// Throws when signup is switched on but cannot work. Called at startup, so the deployment
    /// that misconfigured it finds out before a customer does.
    /// </summary>
    /// <param name="portal">
    /// The shared public-URL settings. Verification links are built from them, and a signup
    /// that sends a broken link creates accounts nobody can open — so signup may not be enabled
    /// without them.
    /// </param>
    public void Validate(PortalOptions portal)
    {
        if (!Enabled)
        {
            return;
        }

        if (!portal.IsConfigured)
        {
            throw new InvalidOperationException(
                $"{PortalOptions.SectionName}:PublicBaseUrl is required when "
                + $"{SectionName}:Enabled is true. Verification links are built from it, and a "
                + "signup that sends a broken link creates accounts nobody can open.");
        }

        if (VerificationValidForHours is <= 0 or > 168)
        {
            throw new InvalidOperationException(
                $"{SectionName}:VerificationValidForHours must be between 1 and 168.");
        }

        if (ResendCooldownMinutes is < 0 or > 1440)
        {
            throw new InvalidOperationException(
                $"{SectionName}:ResendCooldownMinutes must be between 0 and 1440.");
        }
    }
}
