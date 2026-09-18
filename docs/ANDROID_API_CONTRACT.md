# Android ↔ API Contract

Derived by reading the .NET controllers, not designed independently. Every endpoint below
exists in `src/Zazi.Api/Controllers/`. Where Android needs something the server does not
provide, it is listed under [Gaps](#gaps) rather than invented client-side.

Base path: `/api/v1`. Bearer token on everything except login, refresh and health.

---

## The one rule that shapes everything

**The client never sends an organization id.** Tenancy is derived server-side from the
access token. No request model in `src/Zazi.Api/Models/` carries an `organizationId`
field, and the Android DTOs mirror that — `SyncWireContractTest` asserts the serialised
request contains no such key.

Client-supplied `branchId`, `deviceId` and `sessionId` are *requests*, validated server-side
against the caller's tenant. A cross-tenant id returns the same response as a nonexistent
one, so ids cannot be probed.

---

## Authentication

| Endpoint | Method | Auth | Notes |
|---|---|---|---|
| `/auth/login` | POST | anonymous | `{ email, password, deviceIdentifier? }` |
| `/auth/refresh` | POST | anonymous | `{ refreshToken }` — rotates the token |
| `/auth/me` | GET | bearer | Current user, roles, branch |
| `/auth/staff` | POST | `staff.manage` | Not used by the agent app |

Login returns `{ accessToken, refreshToken, expiresAtUtc, user }`.

**Device binding.** Passing `deviceIdentifier` binds the session to a registered device.
A revoked device is refused at login *and* at refresh — the server re-checks per request, so
revocation takes effect without waiting for token expiry.

**Refresh rotation.** Each refresh returns a new refresh token and invalidates the old one.
Reuse of a consumed token revokes the whole family. The client must therefore persist the
new token before using it, and must never retry a refresh with an already-spent token.

**Give-up rule.** If refresh fails with 401/403, the session or device was revoked. Clear
local auth state and require login. Do **not** re-enrol a new device identity automatically —
that would turn a deliberate revocation into a silent re-admission.

---

## Device registration

| Endpoint | Method | Policy |
|---|---|---|
| `/devices` | GET | `device.read` |
| `/devices` | POST | `device.manage` |

`POST /devices` takes `{ name, deviceIdentifier, platform, network, role, appVersion, osVersion }`
and returns the server-assigned device id.

**A locally stored device id is not proof of authorization.** It is an identifier, not a
credential; the server re-validates the device on every sync batch.

Note `device.manage` excludes `AGENT`. An agent cannot self-enrol — a manager or admin
registers the handset. The Android flow must account for that rather than assuming an agent
can complete setup alone.

### Worker activation — the path a handset actually takes

`POST /devices/activate` is **anonymous**, and it is what a worker uses. `POST /devices/enrol`
requires a token and therefore an account, which is the obstacle activation exists to remove:
a worker operating a business phone should not need credentials of their own.

```
{ code, deviceIdentifier, name, platform, network, appVersion, osVersion }
```

Send no organization, branch or role. The server reads them from the code. The response is a
real session plus the worker's name, branch and business:

```
{ session: { accessToken, refreshToken, expiresAtUtc, user }, deviceId, deviceName,
  branchId, branchName, organizationName, workerName }
```

Two client-side consequences that have already caused a defect:

- **`user.email` is null for a worker.** They have no account, so the field is absent rather
  than empty. A required-string deserialization here fails the whole activation and surfaces
  as an opaque error with nothing pointing at the cause.
- **Activation cannot work offline.** Only the server can say who a worker is. Everything
  after activation keeps the existing offline behaviour; this one step does not.

`401` means the code cannot be used and does not say why — invalid, expired, revoked, spent,
attempt-limited and worker-disabled are deliberately indistinguishable. Show one message and
name the recovery: ask the owner for a new code. `409` means this handset is already
registered, which needs the owner to reset the device instead.

---

## Sync — the endpoint the engine is built around

`POST /api/v1/sync/transactions`, policy `sync.submit` (includes `AGENT`).

Full contract in [BATCH_SYNC.md](BATCH_SYNC.md). What the client must honour:

- **Max batch 100**, server-configurable. Over-size is refused whole with `413` carrying
  `maxBatchSize`; the client re-chunks from that value rather than a hardcoded constant.
- **`200 OK` even on partial failure.** Outcome is per item, never inferred from the status.
- **`Duplicate` is a success.** It carries the original `transactionId`. This is the
  lost-response path: mark the row synced, do not retry.
- **`clientTransactionId` is never regenerated on retry.** Regenerating after a timeout is
  precisely how one transaction becomes two.
- Per-item `category` and `isRetryable` tell the client whether retrying can ever succeed.

Amounts travel as **strings** so no `Double` can enter the chain.

`transactionType`: 0 CashIn · 1 CashOut · 2 Transfer · 3 Reversal · 4 Commission ·
5 Adjustment · 6 Unknown. `sourceType`: 0 AndroidSms · 1 ManualEntry · 2 GsmGateway ·
3 Relay · 4 ProviderApi. Both mirrored in `TransactionType.wireValue` and pinned by test.

---

## Sessions

| Endpoint | Method | Policy |
|---|---|---|
| `/sessions/{id}` | GET | `session.read` |
| `/sessions/open` | POST | `session.manage` |
| `/sessions/close` | POST | `session.manage` |

**Session-close rule (server-authoritative).** A transaction is judged against the session
window it claims, not when it arrives. Work captured at 14:00 in a session closed at 17:00
still posts when the device reconnects at 19:00. Only an event time *outside* the window is
refused, with `SESSION_NOT_OPEN_AT_EVENT_TIME`.

The client must not implement its own recovery-window arithmetic. Duplicating that rule is
how the two diverge.

---

## Transactions and dashboard

| Endpoint | Method | Policy | Notes |
|---|---|---|---|
| `/transactions` | GET | `transaction.read` | Paged: `page`, `pageSize` (max 200) |
| `/transactions` | POST | `transaction.record` | Single record; the agent app uses batch sync instead |
| `/dashboard/organization` | GET | `dashboard.organization` | **Excludes `AGENT`** |
| `/alerts` | GET | `alert.read` | Excludes `AGENT` |
| `/branches` | GET | `branch.read` | |
| `/organizations/current` | GET | `organization.read` | |

`GET /transactions` returns `{ items, page, pageSize, totalCount, hasMore }`.

---

## Errors

RFC 7807 problem+json for request-level failures, always carrying `correlationId`:

```json
{ "status": 413, "title": "The synchronisation batch is too large.",
  "type": "https://zazi.app/problems/413",
  "correlationId": "…", "maxBatchSize": 100, "submitted": 250 }
```

| Status | Client action |
|---|---|
| 400 | Permanent — dead-letter, do not retry unchanged |
| 401 | Refresh once, then retry; repeated failure ⇒ clear auth |
| 403 | Revoked or not permitted — clear auth, require login |
| 409 | Conflict — surface, do not retry |
| 413 | Re-chunk using `maxBatchSize` |
| 429 | Honour `Retry-After`; always retryable |
| 5xx | Transient — exponential backoff |

Internal exception detail is never returned; it is logged server-side against the
correlation id.

## Rate limits

- Auth endpoints: 10/min per IP
- Authenticated traffic: 300/min per **user**

The limit is on requests, not transactions, so a 48-hour backlog drains in batches of 100
without throttling. Note it is per user, not per device: several devices on one account share
the allowance.

---

## Gaps

Things Android needs that the server does not yet provide. **Not worked around client-side.**

1. **No device self-enrolment for agents.** `POST /devices` requires `device.manage`, which
   excludes `AGENT`. Either a manager provisions each handset, or the server needs an
   enrolment-code flow. Blocks unattended agent setup.
2. **No `GET /devices/me`.** A device cannot ask whether it is still trusted; it discovers
   revocation only by being refused. A cheap liveness check would let the app clear state
   proactively instead of stranding an outbox.
3. **No conflict-resolution endpoint.** `SyncConflict` rows are recorded server-side but
   there is no API to list or resolve them, so the app can report a conflict but not help
   resolve it.
4. **No `GET /sync/config`.** `MaxBatchSize` is discoverable only by being rejected with
   413. Workable, but it costs one wasted oversized request per install.
5. **`POST /sms/capture` is server-side parsing.** The Android engine parses locally and
   submits through batch sync instead, so this endpoint is unused by the app. Two parser
   implementations now exist (server and client) and must be kept in step; the shared
   fingerprint algorithm is what keeps them honest.

None of these block the offline engine. Items 1 and 2 block a complete agent onboarding flow.
