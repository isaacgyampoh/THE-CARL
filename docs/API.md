# API

Base path: `/api/v1`. JSON only. Bearer-token authentication.

## Conventions

- **Authentication is required by default.** The authorization fallback policy requires an
  authenticated user, so any endpoint not listed as anonymous returns `401` without a token.
- **No endpoint accepts an `organizationId`.** The tenant comes from the access token. See
  [MULTI_TENANCY.md](MULTI_TENANCY.md).
- Errors are RFC 7807 `application/problem+json`. Internal exception detail is never
  returned; correlate using the `correlationId` field.
- Every response carries `X-Correlation-Id`. Clients may supply one (≤64 chars,
  `[A-Za-z0-9_-]`); otherwise one is generated.

### Status codes

| Code | Meaning |
|---|---|
| 400 | Validation failure |
| 401 | Missing, malformed, or expired token |
| 403 | Authenticated but not permitted, **including every cross-tenant attempt** |
| 404 | Resource does not exist within the caller's tenant |
| 409 | Conflict with current state (duplicate email, closed session) |
| 429 | Rate limit exceeded |
| 500 | Unexpected server error |

Cross-tenant access returns `403`, never `404`, so entity ids cannot be probed.

---

## Anonymous endpoints

| Method | Path | Notes |
|---|---|---|
| GET | `/health` | Liveness |
| GET | `/ready` | Readiness; `503` when the database is unreachable |
| GET | `/api/v1/status` | Service name only — no version or backing-store detail |
| POST | `/api/v1/auth/login` | Rate limited per IP (10/min) |
| POST | `/api/v1/auth/refresh` | Rate limited per IP (10/min) |
| POST | `/api/v1/devices/activate` | Worker activation. Rate limited per IP (10/min) |

`POST /devices/activate` is the only anonymous endpoint that can create a session, so it is
worth being precise about what it does and does not accept.

It takes `{ code, deviceIdentifier, name, platform, network, appVersion, osVersion }` — facts
the handset knows about itself, and nothing else. There is **no** organization, branch, role
or user field, and that omission is the security property: the server reads all of them from
the code, so a client that could name its own organization could join any tenant it liked.

The code is looked up globally by SHA-256 hash rather than within a tenant, because there is
no caller to take a tenant from. That is safe because the lookup key is a hash of eighty bits
of entropy — there is nothing to enumerate — and every other check is the same one the
authenticated enrolment path makes.

On success it creates the device, marks the code redeemed and returns a real session — the
same `AuthTokenResult` login returns — plus the worker's name, branch and business for the
confirmation screen. It carries nothing about the code and nothing about other users.

`401` covers invalid, expired, revoked, already-used, attempt-limited, worker-disabled and
branch-mismatched alike, with an identical body. Distinguishing them would tell an
unauthenticated caller which codes exist.

---

## Authenticated endpoints

Authenticated traffic is rate limited per user (300/min).

### Auth

| Method | Path | Policy |
|---|---|---|
| GET | `/api/v1/auth/me` | any authenticated user |
| GET | `/api/v1/auth/users` | `staff.manage` — branch managers see only their branch |
| POST | `/api/v1/auth/staff` | `staff.manage` — cannot grant organization-wide roles unless the caller has them |
| POST | `/api/v1/auth/workers` | `staff.manage` — creates a worker with no email and no password, who activates by code. Branch-scoped; organization-wide roles are refused |

### Organizations and branches

| Method | Path | Policy |
|---|---|---|
| GET | `/api/v1/organizations` | `platform.administration` |
| POST | `/api/v1/organizations` | `platform.administration` |
| GET | `/api/v1/organizations/current` | `organization.read` |
| GET | `/api/v1/branches` | `branch.read` |
| POST | `/api/v1/branches` | `branch.manage` |

### Transactions

| Method | Path | Policy |
|---|---|---|
| GET | `/api/v1/transactions` | `transaction.read` |
| POST | `/api/v1/transactions` | `transaction.record` |

`GET` is paged and returns `PagedResult<TransactionDto>`
(`items`, `page`, `pageSize`, `totalCount`, `hasMore`). Query: `page`, `pageSize`
(default 50, **clamped to 200**), `branchId`, `fromUtc`, `toUtc`. There is no unbounded
listing: an organization's history is expected to reach tens of millions of rows.

Branch-scoped callers are pinned to their own branch regardless of `branchId`.

`POST` forces `source = Manual` and takes the agent from the token; `agentId` and `source`
in the body are ignored.

### Sessions, reconciliation, devices, alerts, sync, SMS

| Method | Path | Policy |
|---|---|---|
| GET | `/api/v1/sessions/{sessionId}` | `session.read` |
| POST | `/api/v1/sessions/open` | `session.manage` |
| POST | `/api/v1/sessions/close` | `session.manage` |
| POST | `/api/v1/reconciliation/sessions/{sessionId}/reconcile` | `reconciliation.perform` |
| GET | `/api/v1/devices` | `device.read` |
| POST | `/api/v1/devices` | `device.manage` |
| GET | `/api/v1/alerts` | `alert.read` |
| POST | `/api/v1/alerts/thresholds` | `alert.manage` |
| POST | `/api/v1/alerts/evaluate` | `alert.manage` |
| POST | `/api/v1/sync/queue` | `sync.submit` |
| GET | `/api/v1/sync/pending` | `sync.submit` |
| POST | `/api/v1/sync/process` | `sync.administer` |
| POST | `/api/v1/sms/capture` | `evidence.submit` |
| GET | `/api/v1/dashboard/organization` | `dashboard.organization` |

Reconciliation figures are sent in the **request body**, not the query string: query
strings are routinely written to access and proxy logs, and a branch's cash position does
not belong there.

---

## Breaking changes in Phase 10

| Before | After |
|---|---|
| All endpoints anonymous | All endpoints require authentication |
| `?organizationId=` on most endpoints | Removed — taken from the token |
| `GET /branches/organizations/{organizationId}` | `GET /branches` |
| `GET /dashboard/organizations/{organizationId}` | `GET /dashboard/organization` |
| `POST /auth/register` (anonymous) | `POST /auth/staff` (requires `staff.manage`) |
| `GET /transactions` returned every row | Returns a page, max 200 |
| Reconcile figures in query string | Moved to the request body |
| Request bodies carried `organizationId`, `agentId`, `userId` | Removed; derived from the token |
