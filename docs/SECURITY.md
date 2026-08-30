# Security

Zazi treats mobile money operations as sensitive operational data subject to tenant
isolation, auditable actions, and data minimisation.

Zazi never initiates, authorises, executes, or moves money. Every financial row is
evidence-based accounting data.

---

## What is implemented today

### Authentication

| Control | Status | Detail |
|---|---|---|
| Password hashing | Implemented | PBKDF2-HMAC-SHA256, 600,000 iterations, 16-byte random salt, 32-byte derived key |
| Password comparison | Implemented | `CryptographicOperations.FixedTimeEquals` — no short-circuit compare |
| Password strength | Implemented | ≥12 characters and ≥3 of {upper, lower, digit, symbol} |
| Account lockout | Implemented | 5 consecutive failures → 15-minute lockout, raises a `MULTIPLE_FAILED_LOGINS` alert |
| Access tokens | Implemented | JWT HS256, 30-second clock skew, issuer and audience validated |
| Refresh tokens | Implemented | 32 random bytes, stored only as a SHA-256 hash |
| Refresh rotation | Implemented | Each redemption revokes the presented token and issues a replacement in the same family |
| Reuse detection | Implemented | Presenting an already-rotated token revokes the entire token family |
| Signing key | Implemented | No fallback key exists. The API refuses to start without `Jwt:Key` / `THECARL_JWT_KEY` (≥32 bytes) outside Development |
| MFA | **Not implemented** | Architecture groundwork only — no second factor is enforced |
| Security-stamp revocation | **Partial** | The stamp is issued as a claim but is not yet re-checked per request, so an issued access token stays valid until it expires |

### Authorization

- **Deny by default.** `FallbackPolicy` requires an authenticated user, so an endpoint that
  omits an explicit policy is still protected. A newly added controller cannot be
  accidentally public.
- Seven canonical roles: `PLATFORM_ADMIN`, `OWNER`, `ORGANIZATION_ADMIN`, `BRANCH_MANAGER`,
  `SUPERVISOR`, `AGENT`, `AUDITOR`. Legacy names are mapped by `CarlRoles.Normalize`;
  unrecognised names are rejected rather than defaulted.
- Capability policies live in one place, `CarlPolicies.RolesByPolicy`.
- `AUDITOR` appears in no mutating policy — auditors are structurally read-only.
- Roles are issued as **one claim per role**. A single comma-joined claim value matches no
  role at all, so multi-role users would silently lose all access.

### Tenant isolation

See [MULTI_TENANCY.md](MULTI_TENANCY.md). In short: `OrganizationId` comes only from the
access token, never from request input.

### Transport and headers

`X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`,
`Cross-Origin-Resource-Policy`, `Permissions-Policy`, and
`Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` (this is a JSON API
that never returns markup). HSTS and HTTPS redirection are enabled outside Development.

`X-XSS-Protection` was deliberately **removed**: it is deprecated, modern browsers ignore
it, and its filter has historically introduced vulnerabilities of its own.

### Rate limiting

| Policy | Partition | Limit |
|---|---|---|
| `auth` | Client IP | 10 requests / minute |
| `tenant` | Authenticated user id | 300 requests / minute |

Per-user partitioning means one busy tenant cannot exhaust another tenant's allowance.

### Error handling and observability

- All exceptions are translated to RFC 7807 problem responses. Exception messages and
  stack traces are logged server-side and never returned to the client.
- Every request carries a correlation id (`X-Correlation-Id`), echoed on the response and
  attached to the log scope. Inbound ids are length- and charset-constrained before being
  logged, because unbounded caller-controlled strings in logs are a log-injection vector.
- Tenant-denial and authentication failures log at `Warning` so they are alertable.

### Secrets

No secret is committed. `appsettings.json` contains an empty connection string and no
signing key; both come from environment variables or user-secrets.

---

## Known gaps

These are **not** implemented and must not be described as production-ready:

- MFA enforcement.
- Per-request security-stamp validation (immediate session revocation).
- Device binding of access tokens — a token is not tied to the device that obtained it.
- Field-level encryption at rest. Encryption currently depends on the database volume.
- Automated retention/erasure of raw SMS text.
- CSRF defences — not currently required because the API is bearer-token only and sets no
  cookies. This must be revisited if cookie authentication is ever added.
- The `IdempotencyKey` unique constraint is enforced by PostgreSQL only. The EF InMemory
  provider used in tests does not enforce unique indexes, so tests cannot prove the
  concurrent-duplicate case. That requires a PostgreSQL-backed integration test.

---

## Verification

Authorization and isolation are covered by executable tests, not assertion in prose:

- `tests/Zazi.IntegrationTests/AnonymousAccessTests.cs` — every protected endpoint
  rejects anonymous callers; operational endpoints stay open; `/api/v1/status` discloses no
  version or backing-store detail.
- `tests/Zazi.IntegrationTests/TenantIsolationTests.cs` — cross-tenant reads and writes.
- `tests/Zazi.IntegrationTests/RoleAuthorizationTests.cs` — role/capability matrix and
  privilege-escalation attempts.
- `tests/Zazi.UnitTests/AuthenticationTests.cs` — hashing, rotation, reuse detection,
  lockout, password strength, role normalisation.

Run with `dotnet test`.

## Web session security

The browser session is a cookie, and a cookie has no natural expiry the way a short-lived
access token does. Three controls close that gap.

**Revocation reaches an open session.** `OnValidatePrincipal` re-reads the account on every
request and rejects the cookie when the account is gone, deactivated, or its security stamp
has been rotated. Without it, revoking a user would leave their browser working for the
remaining eight hours of the cookie — a weaker guarantee than the API gives for the same act,
where a refused refresh ends the session within one token lifetime. It reads the stamp the
existing revocation service already rotates; there is no second revocation mechanism.

**Sign-in and sign-out are antiforgery-checked.** Login CSRF is the one that matters: without
a token check, any site could post a login form and silently sign a manager into an account
the attacker controls, after which the manager reviews the attacker's figures believing they
are their own branch's. Signing someone *in* is as dangerous here as signing them out.

**Sign-in is rate limited per IP**, matching the API's credential policy of ten attempts per
minute. Account lockout already blunts a targeted guess against one account; the limiter
blunts a spray across many accounts from one source, which lockout alone does not see.

Passwords are verified only by `IAuthService`, against the same user store, the same PBKDF2
parameters and the same lockout counters as the API. The cookie records who was verified. It
never becomes a second way to prove identity, and no password is written to it.
