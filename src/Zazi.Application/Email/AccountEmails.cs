namespace Zazi.Application.Email;

/// <summary>
/// Messages about an existing account, as opposed to one being created.
/// </summary>
/// <remarks>
/// Shares <see cref="EmailLayout"/> with <see cref="OnboardingEmails"/>, so the two look like
/// they came from the same product and a change to the shell reaches both.
/// </remarks>
public static class AccountEmails
{
    /// <summary>The reset link, for someone who asked to reset their password.</summary>
    /// <remarks>
    /// Says nothing about the account beyond that it exists — no name, no business, no branch.
    /// The person requesting a reset has not proved they are the account holder yet, and until
    /// they open this link the only thing they are known to control is the mailbox.
    /// </remarks>
    public static EmailMessage PasswordReset(string toAddress, string resetUrl, int validForMinutes)
    {
        var validity = Describe(validForMinutes);

        var html = EmailLayout.Page_(string.Join("\n", new[]
        {
            EmailLayout.Paragraph("Someone asked to reset the password for this Zazi account."),
            EmailLayout.Action("Choose a new password", resetUrl),
            EmailLayout.Small($"This link works once and expires in {EmailLayout.Encode(validity)}."),
            EmailLayout.Footnote(
                "If you did not ask for this, you can ignore this message — your password has "
                + "not changed and nobody has been given access. Nothing happens until the link "
                + "above is opened.")
        }));

        var text = $"""
            Zazi

            Someone asked to reset the password for this Zazi account.

            {resetUrl}

            This link works once and expires in {validity}.

            If you did not ask for this, you can ignore this message — your password has not
            changed and nobody has been given access. Nothing happens until the link above is
            opened.
            """;

        return new EmailMessage(toAddress, "Reset your Zazi password", html, text);
    }

    /// <summary>
    /// Confirmation that a password was changed.
    /// </summary>
    /// <remarks>
    /// Sent after the fact, and worth sending: it is how the real account holder finds out if
    /// someone else completed a reset against their mailbox. It carries no link to click,
    /// because a message announcing a security event is exactly the shape a phishing message
    /// wants to take.
    /// </remarks>
    public static EmailMessage PasswordChanged(string toAddress, string signInUrl)
    {
        var html = EmailLayout.Page_(string.Join("\n", new[]
        {
            EmailLayout.Paragraph("The password for this Zazi account has been changed."),
            EmailLayout.Paragraph(
                "Every device and browser that was signed in has been signed out. You will need "
                + "to sign in again with the new password."),
            EmailLayout.Small($"Sign in at {EmailLayout.Encode(signInUrl)}"),
            EmailLayout.Footnote(
                "If this was not you, contact whoever administers your Zazi account immediately. "
                + "Whoever changed the password has access to this mailbox.")
        }));

        var text = $"""
            Zazi

            The password for this Zazi account has been changed.

            Every device and browser that was signed in has been signed out. You will need to
            sign in again with the new password.

            Sign in at {signInUrl}

            If this was not you, contact whoever administers your Zazi account immediately.
            Whoever changed the password has access to this mailbox.
            """;

        return new EmailMessage(toAddress, "Your Zazi password was changed", html, text);
    }

    private static string Describe(int minutes) => minutes switch
    {
        < 60 => $"{minutes} minutes",
        60 => "1 hour",
        _ when minutes % 60 == 0 => $"{minutes / 60} hours",
        _ => $"{minutes} minutes"
    };
}
