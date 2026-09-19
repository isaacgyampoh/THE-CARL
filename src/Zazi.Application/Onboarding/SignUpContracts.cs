namespace Zazi.Application.Onboarding;

/// <summary>What a new business supplies to open an account.</summary>
/// <remarks>
/// Deliberately short. Everything else a tenant needs — branches beyond the first, staff,
/// devices — is configured from the portal afterwards, by someone who can see what they are
/// choosing. A long signup form is a long opportunity to abandon signing up.
/// </remarks>
public sealed record SignUpRequest(
    string BusinessName,
    string FullName,
    string Email,
    string Password,
    string? PhoneNumber = null);

/// <summary>
/// The outcome of a signup attempt, as far as the caller is permitted to know.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no "that address is already registered" case. The sign-in form is
/// already careful not to reveal which addresses exist, and a signup form that happily confirms
/// it would undo that: anyone could enumerate Zazi's customers by submitting addresses and
/// reading the response.
/// </para>
/// <para>
/// So a duplicate produces the same result as a success, and the difference is carried by the
/// email instead — which only reaches the person who actually owns the address.
/// </para>
/// </remarks>
public enum SignUpOutcome
{
    /// <summary>
    /// The request was accepted. An email has been sent — either a verification link, or, if
    /// the address was already registered, a note saying so. The caller cannot tell which.
    /// </summary>
    VerificationSent = 0,

    /// <summary>
    /// The account was created but the email could not be sent. Distinct because the person
    /// must be told to try resending rather than told to check an inbox that will stay empty.
    /// </summary>
    CreatedButEmailFailed = 1
}

/// <summary>The result of a signup attempt.</summary>
public sealed record SignUpResult(SignUpOutcome Outcome);

/// <summary>Why a verification link did not work.</summary>
public enum EmailVerificationOutcome
{
    /// <summary>Verified. The owner may now sign in.</summary>
    Verified = 0,

    /// <summary>
    /// No account is waiting on this token. Covers a token that never existed, one already
    /// used, and one belonging to an account since deleted — all indistinguishable on purpose.
    /// </summary>
    InvalidOrAlreadyUsed = 1,

    /// <summary>The token was real but too old. A fresh one can be requested.</summary>
    Expired = 2
}

/// <summary>The result of following a verification link.</summary>
public sealed record EmailVerificationResult(EmailVerificationOutcome Outcome)
{
    public bool Succeeded => Outcome == EmailVerificationOutcome.Verified;
}

/// <summary>
/// Self-service onboarding: a business creating its own account, without anyone at Zazi.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>IAuthService</c>, which exists to authenticate people who already have
/// accounts and to let an administrator create accounts for others. This creates the tenant
/// itself — organization, first branch and owner together — and it is the one path into the
/// system that runs with no caller identity at all, so its rules are its own.
/// </para>
/// <para>
/// The central rule: <b>nothing created here can be signed into until the address is
/// verified.</b> The owner is written inactive, and only verification activates them. An
/// account that works before anyone proves they own the address is an account anyone can open
/// in someone else's name.
/// </para>
/// </remarks>
public interface ISignUpService
{
    /// <summary>
    /// Creates an organization, its first branch and its inactive owner, then emails a
    /// verification link.
    /// </summary>
    /// <remarks>
    /// The organization, branch and owner are written in one transaction. A partial tenant —
    /// an organization with no owner, or an owner with no branch — is not something any later
    /// screen knows how to repair.
    /// </remarks>
    Task<SignUpResult> SignUpAsync(SignUpRequest request, CancellationToken cancellationToken = default);

    /// <summary>Redeems a verification token, activating the owner it belongs to.</summary>
    Task<EmailVerificationResult> VerifyEmailAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a fresh verification link, if the address belongs to an unverified account and one
    /// was not sent too recently.
    /// </summary>
    /// <remarks>
    /// Returns nothing. Reporting whether the address matched would make this the enumeration
    /// oracle that <see cref="SignUpAsync"/> is careful not to be.
    /// </remarks>
    Task ResendVerificationAsync(string email, CancellationToken cancellationToken = default);
}
