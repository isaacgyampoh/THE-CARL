namespace TheCarl.Application.Security;

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
