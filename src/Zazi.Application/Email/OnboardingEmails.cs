namespace Zazi.Application.Email;

/// <summary>
/// The messages Zazi sends while someone is signing up.
/// </summary>
/// <remarks>
/// Rendered through <see cref="EmailLayout"/>, which is shared with <see cref="AccountEmails"/>
/// so the two look like they came from the same product.
/// </remarks>
public static class OnboardingEmails
{
    /// <summary>The verification link, for someone who has just signed up.</summary>
    public static EmailMessage Verification(string toAddress, string businessName, string verificationUrl)
    {
        var safeName = EmailLayout.Encode(businessName);

        var html = EmailLayout.Page_(string.Join("\n", new[]
        {
            EmailLayout.Paragraph($"Confirm your email address to finish setting up <strong>{safeName}</strong>."),
            EmailLayout.Action("Confirm email address", verificationUrl),
            EmailLayout.Small("This link works once and expires in 24 hours."),
            EmailLayout.Footnote(
                "If you did not sign up for Zazi, you can ignore this message. No account is "
                + "usable until this link is opened.")
        }));

        var text = $"""
            Zazi

            Confirm your email address to finish setting up {businessName}.

            {verificationUrl}

            This link works once and expires in 24 hours.

            If you did not sign up for Zazi, you can ignore this message. No account is
            usable until this link is opened.
            """;

        return new EmailMessage(toAddress, "Confirm your email address", html, text);
    }

    /// <summary>
    /// Sent when someone signs up with an address that already has an account.
    /// </summary>
    /// <remarks>
    /// This message is why signup can refuse a duplicate without saying so. The response to the
    /// browser is identical either way, so nobody can enumerate customers through the form; the
    /// real account holder learns what happened here, where only they can read it.
    /// </remarks>
    public static EmailMessage AlreadyRegistered(string toAddress, string signInUrl)
    {
        var html = EmailLayout.Page_(string.Join("\n", new[]
        {
            EmailLayout.Paragraph(
                "Someone tried to create a Zazi account with this email address, but one already exists."),
            EmailLayout.Action("Sign in instead", signInUrl),
            EmailLayout.Footnote(
                "If that was you, sign in above. If it was not, nothing has changed and no new "
                + "account was created — but it is worth knowing that someone has your address.")
        }));

        var text = $"""
            Zazi

            Someone tried to create a Zazi account with this email address, but one
            already exists.

            Sign in instead: {signInUrl}

            If that was you, sign in above. If it was not, nothing has changed and no new
            account was created — but it is worth knowing that someone has your address.
            """;

        return new EmailMessage(toAddress, "You already have a Zazi account", html, text);
    }
}
