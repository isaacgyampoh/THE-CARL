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
    /// <summary>
    /// Sent when someone signs up with an address that already has an account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The likeliest reader is not an attacker but the owner of the address, signing up again
    /// because the first attempt did not work — usually a mistyped password. Their new password
    /// was not saved: a second signup keeps the first account exactly as it was. The previous
    /// version of this email said only "sign in instead", which sent them to the sign-in page
    /// with the password that had just been discarded, to be told it was incorrect.
    /// </para>
    /// <para>
    /// So the primary action is setting a new password, which works whether or not they ever
    /// confirmed the address, and the email says plainly which password is in force.
    /// </para>
    /// </remarks>
    public static EmailMessage AlreadyRegistered(string toAddress, string signInUrl, string forgotPasswordUrl)
    {
        var html = EmailLayout.Page_(string.Join("\n", new[]
        {
            EmailLayout.Paragraph(
                "You tried to create a Zazi account with this email address, but one already exists."),
            EmailLayout.Paragraph(
                "No new account was created, and the password you just typed was not saved — "
                + "your account still has the password from when you first signed up."),
            EmailLayout.Paragraph(
                "Not sure what that password was? Set a new one. It works even if you never "
                + "confirmed your email, and it confirms it for you."),
            EmailLayout.Action("Set a new password", forgotPasswordUrl),
            EmailLayout.Footnote(
                $"Remember it after all? Sign in at {signInUrl}. If you did not try to sign up, "
                + "nothing has changed — but it is worth knowing that someone has your address.")
        }));

        var text = $"""
            Zazi

            You tried to create a Zazi account with this email address, but one
            already exists.

            No new account was created, and the password you just typed was not
            saved — your account still has the password from when you first signed up.

            Not sure what that password was? Set a new one. It works even if you never
            confirmed your email, and it confirms it for you:
            {forgotPasswordUrl}

            Remember it after all? Sign in: {signInUrl}

            If you did not try to sign up, nothing has changed — but it is worth
            knowing that someone has your address.
            """;

        return new EmailMessage(toAddress, "You already have a Zazi account", html, text);
    }
}
