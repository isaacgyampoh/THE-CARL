namespace Zazi.Application.Security;

/// <summary>Settings for password reset.</summary>
public sealed class PasswordResetOptions
{
    public const string SectionName = "PasswordReset";

    /// <summary>
    /// How long a reset link stays valid.
    /// </summary>
    /// <remarks>
    /// Short — far shorter than email verification's 24 hours. A verification link only proves
    /// an address; this one changes a password on an account that already holds financial
    /// records, and it sits in a mailbox for as long as it is valid.
    /// </remarks>
    public int ValidForMinutes { get; set; } = 30;

    /// <summary>The shortest gap between reset emails to one account.</summary>
    public int RequestCooldownMinutes { get; set; } = 2;

    /// <summary>
    /// The floor, in milliseconds, on how long a reset request takes to answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A request for a known address does real work — it writes a token and calls the email
    /// provider — while an unknown one does a single lookup. Without a floor, the response time
    /// says which happened, and the whole point of returning an identical message is that
    /// nobody can tell.
    /// </para>
    /// <para>
    /// This bounds the difference rather than eliminating it: a request that genuinely takes
    /// longer than the floor still takes longer. It is set above the normal cost of a send so
    /// that the ordinary case is indistinguishable.
    /// </para>
    /// </remarks>
    public int MinimumResponseMilliseconds { get; set; } = 900;

    public void Validate()
    {
        if (ValidForMinutes is <= 0 or > 1440)
        {
            throw new InvalidOperationException(
                $"{SectionName}:ValidForMinutes must be between 1 and 1440. A reset link is a "
                + "password change waiting in a mailbox; it should not be valid for long.");
        }

        if (RequestCooldownMinutes is < 0 or > 1440)
        {
            throw new InvalidOperationException($"{SectionName}:RequestCooldownMinutes must be between 0 and 1440.");
        }

        if (MinimumResponseMilliseconds is < 0 or > 10_000)
        {
            throw new InvalidOperationException($"{SectionName}:MinimumResponseMilliseconds must be between 0 and 10000.");
        }
    }
}

/// <summary>Whether a reset link is usable, before anyone is asked to type a new password.</summary>
public enum PasswordResetTokenState
{
    /// <summary>Usable. A new password may be chosen.</summary>
    Valid = 0,

    /// <summary>
    /// No account is waiting on this token. Covers one that never existed, one already used,
    /// and one superseded by a later request — indistinguishable on purpose.
    /// </summary>
    InvalidOrAlreadyUsed = 1,

    /// <summary>Real, but too old. A fresh one can be requested.</summary>
    Expired = 2
}

/// <summary>The outcome of completing a reset.</summary>
public enum PasswordResetOutcome
{
    /// <summary>The password was changed and every existing session was closed.</summary>
    Changed = 0,

    InvalidOrAlreadyUsed = 1,

    Expired = 2,

    /// <summary>The new password does not meet the policy. See <see cref="PasswordResetResult.Problem"/>.</summary>
    PasswordRejected = 3
}

/// <summary>The result of completing a reset.</summary>
public sealed record PasswordResetResult(PasswordResetOutcome Outcome, string? Problem = null)
{
    public bool Succeeded => Outcome == PasswordResetOutcome.Changed;
}

/// <summary>
/// Resetting a forgotten password.
/// </summary>
/// <remarks>
/// <para>
/// Built from the pieces that already exist: <see cref="PasswordPolicy"/> for what a password
/// must be, the infrastructure's PBKDF2 helper for storing it, <c>IEmailSender</c> for the
/// link, <see cref="IIdentityRevocationService"/> for closing sessions afterwards, and the same
/// hashed single-use token pattern email verification uses. Nothing about authentication is
/// re-decided here.
/// </para>
/// <para>
/// The rule that shapes the whole interface: <b>no method reveals whether an address has an
/// account.</b> Requesting a reset returns nothing at all, and the page says the same thing
/// either way. Anything else turns this form into a way to enumerate Zazi's customers, and it
/// is a form that has to be reachable by people who cannot sign in.
/// </para>
/// </remarks>
public interface IPasswordResetService
{
    /// <summary>Whether this deployment can send reset links at all.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Sends a reset link, if the address belongs to an account that can use one.
    /// </summary>
    /// <remarks>
    /// Returns nothing, and takes the same time whichever way it goes. Both are deliberate.
    /// </remarks>
    Task RequestResetAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a token is usable, without consuming it.
    /// </summary>
    /// <remarks>
    /// So the reset page can refuse before asking someone to think of a password. It does not
    /// consume the token: a page load is not a reset, and a link prefetched by a mail client
    /// must not burn the token before its owner clicks it.
    /// </remarks>
    Task<PasswordResetTokenState> InspectTokenAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Consumes the token and sets the new password, closing every existing session.
    /// </summary>
    Task<PasswordResetResult> ResetPasswordAsync(
        string token,
        string newPassword,
        CancellationToken cancellationToken = default);
}
