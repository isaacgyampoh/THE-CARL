namespace Zazi.Application.Security;

/// <summary>
/// Raised when an authenticated caller attempts to reach data outside their tenant or
/// branch scope. Surfaces as HTTP 403 and is always audited.
/// </summary>
public sealed class TenantAccessDeniedException : Exception
{
    public TenantAccessDeniedException(string message) : base(message)
    {
    }

    public TenantAccessDeniedException(Guid actorOrganizationId, Guid requestedOrganizationId)
        : base("The authenticated caller may not access the requested organization.")
    {
        ActorOrganizationId = actorOrganizationId;
        RequestedOrganizationId = requestedOrganizationId;
    }

    public Guid? ActorOrganizationId { get; }
    public Guid? RequestedOrganizationId { get; }
}

/// <summary>
/// Raised when the request carries no usable authenticated identity but reached code
/// that requires one. Surfaces as HTTP 401.
/// </summary>
public sealed class NotAuthenticatedException : Exception
{
    public NotAuthenticatedException(string message = "The request is not authenticated.")
        : base(message)
    {
    }
}

/// <summary>Raised for domain rule violations that should surface as HTTP 409.</summary>
public sealed class ConflictException : Exception
{
    public ConflictException(string message) : base(message)
    {
    }
}

/// <summary>
/// Why a sign-in with the <b>correct</b> password was still refused.
/// </summary>
/// <remarks>
/// <para>
/// Every login failure used to read "Email or password is incorrect", including the ones where
/// the password was right. Three owners who had just signed up were told their password was
/// wrong when the real answer was "confirm your email first" or "wait fifteen minutes", and the
/// only thing the message invited them to do — try the password again — made it worse, because
/// each retry counted towards the lockout.
/// </para>
/// <para>
/// Telling these apart leaks nothing. Each is raised only <i>after</i> the password has been
/// verified, so the only person who can ever see one already holds the credential. "No such
/// account" and "wrong password" still produce one identical message, which is the distinction
/// that matters for enumeration.
/// </para>
/// <para>
/// A subclass of <see cref="UnauthorizedAccessException"/> so every existing caller — the API's
/// exception middleware, which answers 401, among them — behaves exactly as before. Only a
/// caller that chooses to look can tell the reasons apart.
/// </para>
/// </remarks>
public sealed class SignInRefusedException : UnauthorizedAccessException
{
    public SignInRefusedException(SignInRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    public SignInRefusal Reason { get; }
}

public enum SignInRefusal
{
    /// <summary>Signed up, but the confirmation link in the email has not been opened.</summary>
    EmailNotVerified,

    /// <summary>Too many failed attempts; it clears on its own.</summary>
    TemporarilyLocked,

    /// <summary>Deliberately switched off by someone with the authority to.</summary>
    Disabled
}
