# ADR-013: Worker activation without an account, and owner-first authentication

**Status:** Proposed — design only. Nothing in this document is implemented yet.

## Context

Zazi is used by businesses, not individuals. A business owner holds the account; workers
operate handsets in branches. The current authentication model does not reflect that: every
worker needs their own email-and-password account before they can use a device.

### What already exists

An audit of the current system found that most of the machinery a code-based activation flow
needs is already built and is sound:

| Capability | Where | State |
|---|---|---|
| Single-use, time-limited enrolment code | `DeviceEnrollmentCode` | Built |
| SHA-256 storage, plaintext shown once | `DeviceEnrollmentService` | Built |
| Scope (org, branch, role) fixed at issue | `DeviceEnrollmentCode` | Built |
| Attempt limit, expiry, revocation | `DeviceEnrollmentCode.IsRedeemable` | Built |
| Uniform rejection (no code-existence oracle) | `RedeemAsync` | Built |
| `User → Device → AuthSession → RefreshToken` chain | `AuthSession` | Built |
| Revocation authoritative at refresh | `AuthSession`, `RefreshToken` | Built |
| Security-stamp invalidation | `User.SecurityStamp` | Built |
| Device revocation status | `AuthSessionStatus.RevokedByDeviceRevocation` | Built |
| Owner issues/lists/revokes codes | `DevicesController` | Built |
| Android enrolment screen | `EnrolmentScreen`, `SessionState.NeedsEnrolment` | Built |

The code already carries `IntendedUserId`, and redemption already refuses a code whose
intended user does not match the caller.

### The actual gap

`POST /api/v1/devices/enrol` is `[Authorize]`. Both the organization and the redeeming user
are taken from the authenticated principal:

```csharp
RedeemAsync(request, organizationId, redeemingUserId, …)
```

So the worker must already hold a JWT, which means they must already have an email and a
password. The Android state machine encodes the same coupling: `NeedsEnrolment` carries an
`AuthenticatedUser`, so enrolment is by construction something that happens *after* login.

That is the whole problem. It is one structural coupling, not a missing subsystem.

## Decision

Make the activation code the **identity carrier** rather than only a scope carrier, and add
an anonymous activation endpoint beside the existing authenticated one. Do not remove
email/password: it remains the owner's method and the recovery path.

### 1. Activation codes bind a worker

`IntendedUserId` becomes **required** for codes issued for worker activation. The owner
creates the worker first (name, role, branch — no email, no password), then issues a code
bound to that worker. The code then answers all five questions the server must not take from
the client: who, which business, which branch, what role, which device.

Existing codes with a null `IntendedUserId` remain valid for the existing authenticated
flow. This is additive; nothing already issued stops working.

### 2. A new anonymous activation endpoint

`POST /api/v1/devices/activate` — `[AllowAnonymous]`, per-IP rate limited.

It differs from `enrol` in exactly one respect: the code is looked up by hash **globally**
rather than within a caller's organization, because there is no caller. That is safe
precisely because the lookup is by SHA-256 of a high-entropy secret — there is nothing to
enumerate — and it is the only way an unauthenticated device can be placed in a tenant. Every
other check is the existing one, unchanged.

On success it creates the device, marks the code redeemed, and issues a real `AuthSession`
plus refresh family bound to that device — the same objects `login` produces. Nothing
downstream needs to know the session began with a code.

### 3. `User` must tolerate a worker with no credentials

`Email`, `PasswordHash` and `PasswordSalt` are currently non-nullable and effectively
required. A worker created by an owner has none of them. They become optional, with a
constraint that a user has **either** a credential pair **or** an activation-bound identity,
never neither. Login must refuse a credential-less user explicitly rather than falling
through a null check — that is the single most dangerous edge in this change and needs a
test that asserts the refusal, not merely the absence of a crash.

### 4. Owner authentication

Phone + OTP is the intended owner experience. It is **not** part of this change. The backend
has no OTP issuance, no SMS sender, and no rate-limited verification path; `MfaCredential`
exists but is not an OTP flow. Introducing one is a separate ADR with its own threat model,
delivery-provider decision, and cost model. Until then the owner keeps email and password,
which is already hardened (lockout, security stamp, refresh rotation, reuse detection).

Sequencing this second is deliberate: worker activation is the change that removes a real
obstacle for real users, and it does not depend on OTP.

### 5. Local unlock on the handset

After activation the worker should not re-enter anything. A local PIN or biometric unlock is
worth having, but only as a genuine gate on the encrypted session material — the device
credential must be released by the unlock, not merely hidden behind a screen. A PIN that only
dismisses a composable is theatre and must not be built.

## Revocation and offline behaviour

Unchanged, because the existing chain already handles it. Revoking a device or disabling a
worker kills refresh at the next server interaction. An access token remains valid until it
expires, which is the honest limit of any stateless token; the mitigation is its lifetime,
not a claim that revocation is instant.

A fully offline handset cannot be told it has been revoked. It continues to capture into the
outbox — which is correct, because the transactions are the agent's own record of work — and
discovers the revocation on reconnect, at which point it must transition to the revoked
state and stop displaying transaction data. This must be stated plainly in any owner-facing
copy: revoking a stolen phone takes effect when that phone next reaches the server.

## Migration

No destructive step. Ordered:

1. Make `Email`/`PasswordHash`/`PasswordSalt` nullable. Existing rows are unaffected.
2. Add the activation endpoint. The existing `enrol` endpoint keeps working.
3. Add owner-portal worker creation without credentials.
4. Add the Android activation path as a new entry state; keep the login path.
5. Only once activation is verified end to end, stop offering login to workers.

Step 5 is the only one that removes anything, and it is last.

## Consequences

- Workers stop needing accounts they never wanted.
- The client still decides nothing: who, business, branch, role and permissions are all
  resolved server-side from the code.
- One new anonymous endpoint exists, and it is the most attacked surface in the product. It
  needs its own rate limit, and it must never log the submitted code.
- `User` becomes weaker at the type level (nullable credentials) and needs a constraint plus
  tests to keep that from becoming a login bypass.

## What this ADR does not decide

Owner phone/OTP authentication, SMS delivery, PIN/biometric storage design, and worker
self-service recovery. Each needs its own decision record.
