using System.Linq.Expressions;

namespace Zazi.Domain;

/// <summary>
/// The one definition of "signed up, but has not confirmed their email yet".
/// </summary>
/// <remarks>
/// <para>
/// Needed because <see cref="User.IsActive"/> alone means two different things. An owner who
/// signed up and has not opened the confirmation link is inactive; so is a worker an owner
/// deliberately switched off. They must be treated oppositely — the first should be helped in,
/// the second kept out — and a check on <c>IsActive</c> cannot tell them apart.
/// </para>
/// <para>
/// Self-service signup is the only thing that sets <see cref="User.EmailVerificationSentAtUtc"/>,
/// so an account with that set, not yet verified and not active, is exactly one that is waiting
/// on its confirmation link. A deliberately disabled account never had a link sent, and an owner
/// who verified and was later disabled has <see cref="User.EmailVerified"/> set — neither
/// matches.
/// </para>
/// </remarks>
public static class AccountState
{
    /// <summary>Translatable to SQL, for queries.</summary>
    public static readonly Expression<Func<User, bool>> AwaitingEmailVerification =
        u => !u.IsActive && !u.EmailVerified && u.EmailVerificationSentAtUtc != null;

    private static readonly Func<User, bool> AwaitingCompiled = AwaitingEmailVerification.Compile();

    /// <summary>The same rule, for a user already loaded.</summary>
    public static bool IsAwaitingEmailVerification(User user) => AwaitingCompiled(user);

    /// <summary>
    /// Marks the address as proven and the account usable.
    /// </summary>
    /// <remarks>
    /// Called by the confirmation link and by a completed password reset. Both are links sent
    /// to the address and opened by whoever controls it, which is the whole of what verification
    /// ever proved — so a reset completed by an unverified owner verifies them too, rather than
    /// leaving them with a new password they still cannot use.
    /// </remarks>
    public static void MarkEmailVerified(User user, DateTimeOffset now)
    {
        user.EmailVerified = true;
        user.IsActive = true;
        user.EmailVerificationTokenHash = null;
        user.EmailVerificationExpiresAtUtc = null;
        user.UpdatedAt = now;
    }
}
