# Multi-tenancy

Zazi is a multi-tenant SaaS platform. Isolation is enforced server-side, on every
request, from the access token.

## Structure

```
Platform
└── Organization
    ├── Owner / Organization Admin       (organization-wide scope)
    ├── Auditor                          (organization-wide, read-only)
    └── Branch                           (no limit on branch count)
        ├── Branch Manager / Supervisor / Agent
        ├── Devices
        ├── Sessions
        ├── Transactions
        ├── Float accounts
        └── Alerts
```

No branch limit is hardcoded anywhere. An organization may have 1 or 500 branches.

---

## The rule

> `OrganizationId` is read from the authenticated caller's access token and from nowhere
> else. No API endpoint accepts an organization identifier from a route, query string,
> header, or request body.

This is enforced structurally rather than by convention: the client-facing request models
in `src/Zazi.Api/Models/ApiRequests.cs` have **no `OrganizationId` field at all**, so
there is no value for a client to tamper with. Controllers construct the internal
application request using `ICurrentUserContext.OrganizationId`.

The same applies to actor identity. `AgentId` and `UserId` come from the token, so an agent
cannot attribute a transaction or a session to a colleague.

`TransactionSource` is forced to `Manual` on the manual-entry endpoint. A client must never
be able to claim its entry came from verified SMS evidence — manual entries and parsed
evidence carry different trust and have to stay distinguishable.

## Two layers of enforcement

**1. Role scope** — `ICurrentUserContext.HasOrganizationWideScope` distinguishes
organization-wide roles (`PLATFORM_ADMIN`, `OWNER`, `ORGANIZATION_ADMIN`, `AUDITOR`) from
branch-scoped roles. Branch-scoped callers are pinned to their assigned branch: if an agent
asks for another branch's transactions, they receive their own branch's, not an error and
not the other branch's data.

**2. Ownership proof** — role checks alone are insufficient, because a `BRANCH_MANAGER` in
organization A holds the same role as one in organization B. `ITenantGuard` proves that
each entity named by id actually belongs to the caller's organization *before* the entity is
loaded:

| Method | Proves |
|---|---|
| `EnsureBranchInTenantAsync` | The branch exists inside the caller's organization and is in scope |
| `EnsureSessionInTenantAsync` | The session belongs to the caller's organization and branch scope |
| `EnsureDeviceInTenantAsync` | The device belongs to the caller's organization and branch scope |
| `ResolveWritableBranchAsync` | Resolves the branch to write to, falling back to the caller's own |

Every guard query filters on the organization id from the token. "Does not exist in my
tenant" and "does not exist at all" are deliberately indistinguishable to the caller, so
entity ids cannot be probed across tenants. Both return **403**, never 404.

## Data-layer scoping

- Every operational entity carries `OrganizationId`, and indexes lead with it.
- Duplicate detection is organization-scoped. `CapturedSmsMessage` is unique on
  `(OrganizationId, MessageHash)`, not `MessageHash` alone: a global constraint let one
  tenant's captured SMS suppress an identical message in another tenant, which both leaked
  the existence of that tenant's traffic and silently dropped a real transaction.
- Transaction idempotency is unique on `(OrganizationId, IdempotencyKey)`.

## Cross-tenant access

Exactly one capability crosses the tenant boundary: `PLATFORM_ADMIN` listing or creating
organizations. It is the only endpoint that legitimately does so, and therefore the one
guarded most tightly. An `OWNER` cannot enumerate other organizations.

## Verification

`tests/Zazi.IntegrationTests/TenantIsolationTests.cs` exercises these over real HTTP
through the full pipeline: cross-tenant list reads, cross-tenant session reads,
cross-tenant writes, cross-branch writes inside one organization, tokens signed with a
foreign key, and identical SMS text captured by two tenants.
