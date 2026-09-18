namespace Zazi.Application.Security;

/// <summary>
/// Limits a listing to the branches its caller may see.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the rule was written twice and then bypassed a third time. Both
/// <c>GET /auth/users</c> and <c>GET /devices</c> filtered by branch inside the controller, so
/// the owner portal — which calls the same services directly rather than over HTTP — showed a
/// branch manager every worker and every device in the business.
/// </para>
/// <para>
/// A scoping rule that lives in one of several call paths is not a scoping rule. It lives
/// here now, and every caller uses it.
/// </para>
/// </remarks>
public static class BranchScoping
{
    /// <summary>
    /// The branch a listing should be limited to, or <c>null</c> when the caller legitimately
    /// sees the whole organization.
    /// </summary>
    public static Guid? VisibleBranchId(this ICurrentUserContext caller) =>
        caller.HasOrganizationWideScope ? null : caller.BranchId;

    /// <summary>
    /// Returns only the items in the caller's own branch, or all of them when the caller has
    /// organization-wide scope.
    /// </summary>
    /// <remarks>
    /// An item with no branch is organization-wide and is shown only to callers who are too.
    /// Doing it the other way round would leak an organization-wide record — an owner, say —
    /// into every branch manager's list.
    /// </remarks>
    public static IReadOnlyList<T> LimitToVisibleBranch<T>(
        this ICurrentUserContext caller,
        IReadOnlyList<T> items,
        Func<T, Guid?> branchOf)
    {
        var visible = caller.VisibleBranchId();
        return visible is null
            ? items
            : items.Where(item => branchOf(item) == visible).ToList();
    }
}
