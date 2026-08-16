# Offline Transaction Contract

The wire and storage contract between an offline Android device and the API. This document
is the specification the Android client is built against; it is deliberately written before
any UI exists.

THE CARL never initiates, authorises or moves money. Everything below concerns recording
evidence of transactions that already happened elsewhere.

---

## 1. Field reference

**Trust** answers one question: *if a hostile client lied about this field, what happens?*
`UNTRUSTED` fields are accepted for record-keeping but never used for authorisation,
tenancy, or balance direction.

| Field | Type | Required | Origin | Trust | Notes |
|---|---|---|---|---|---|
| `ClientTransactionId` | string(39) | Yes | Client | UNTRUSTED | Replay key. Shape validated; uniqueness enforced by the database. |
| `IdempotencyKey` | — | — | — | — | **Removed.** Split into `ClientTransactionId` and `EvidenceFingerprint`; see §3. |
| `DeviceId` | uuid | No | Client | UNTRUSTED | Must resolve to a device in the caller's org, else 403. |
| `OrganizationId` | uuid | **Never sent** | Server | TRUSTED | Read from the access token. Not a field on any request model. |
| `BranchId` | uuid | No | Client | UNTRUSTED | Validated by `ITenantGuard`; falls back to the caller's own branch. |
| `SessionId` | uuid | No | Client | UNTRUSTED | Must belong to the caller's org and branch. |
| `TransactionType` | enum | Yes | Client | UNTRUSTED | Re-derived server-side from evidence where evidence exists. |
| `Provider` | string(80) | Yes | Client | UNTRUSTED | Normalised server-side. |
| `Amount` | decimal(18,4) | Yes | Client | UNTRUSTED | Positive magnitude only; ≥ ₵0.01. Direction comes from the type. |
| `CustomerPhone` | string(30) | No | Client | UNTRUSTED | Normalised to national significant number for fingerprinting. |
| `TransactionReference` | string(200) | No | Client | UNTRUSTED | Provider reference when present. |
| `TransactionTimestamp` | timestamptz | No | Client | UNTRUSTED | Provider-reported event time. Device clocks are unreliable. |
| `DeviceReceivedAt` | timestamptz | No | Client | UNTRUSTED | Diagnostic only. Never used for ordering or reporting. |
| `ServerReceivedAt` | timestamptz | n/a | **Server** | TRUSTED | Authoritative for ordering, reporting and retention. |
| `EvidenceHash` | string(64) | No | Both | UNTRUSTED | Recomputed server-side; a client value is never taken on faith. |
| `ParserVersion` | string(40) | No | Client | UNTRUSTED | Recorded so old rows stay traceable when templates change. |
| `SourceType` | enum | Yes | Client | UNTRUSTED | `AndroidSms`, `ManualEntry`, `GsmGateway`, `Relay`, `ProviderApi`. |
| `SyncStatus` | enum | n/a | **Client-local** | n/a | Never sent to the server; see §6. |
| `RetryCount` | int | n/a | **Client-local** | n/a | Never sent to the server. |
| `CashDelta` / `FloatDelta` | decimal(18,4) | n/a | **Server** | TRUSTED | Resolved by `LedgerPolicy` at acceptance. |
| `State` | enum | n/a | **Server** | TRUSTED | Lifecycle state; clients cannot assert `Accepted`. |

**Timestamps** are stored as UTC `timestamptz` and rendered in `Africa/Accra` for display.
Server time is never used as a transaction's event time, and device time is never used for
ordering.

---

## 2. ClientTransactionId

Format `CTX-{deviceTag}-{ulid}`, 39 characters. Example:
`CTX-9f3a1c07-01HQ8Z7K3M4N5P6Q7R8S9T0V1W`

- **deviceTag** — 8 hex chars, leading 4 bytes of SHA-256 over the device installation id.
- **ulid** — 26 chars Crockford base32: 48-bit millisecond timestamp + 80 bits of CSPRNG.

Properties that matter, each covered by a test in `ClientTransactionIdTests`:

| Requirement | How it is met |
|---|---|
| Generated with no server contact | Purely local computation. |
| Survives app restart / reboot | No counter, no in-memory sequence to lose. |
| Survives clock changes | Uniqueness rests on 80 random bits, not the timestamp. |
| Distinct across devices | Device tag differs before randomness is even considered. |
| Weeks offline | Nothing expires; the server enforces uniqueness on receipt. |
| Not a bare timestamp | 1,000 ids in one millisecond are still unique. |

The timestamp prefix makes ids lexicographically sortable by creation time, which keeps the
outbox in submission order and keeps the database index append-ordered.

---

## 3. Idempotency boundary

The old single `IdempotencyKey` conflated two different problems. They are now separate,
because they have different correct boundaries.

### 3.1 Replay — `UX_Transactions_Organization_ClientTransactionId`

Unique on `(OrganizationId, ClientTransactionId)`.

Answers: *"is this the same submission arriving again?"* Covers a lost response, a retry
after reboot, and a duplicated outbox drain.

**Why organization and not device:** the ledger is per-organization, and a device can be
re-registered or moved between branches without changing the identity of a transaction it
already submitted. Scoping to the device would additionally mean a device id is required to
enforce correctness, which manual web entry does not have.

**Why not global:** two tenants are independent ledgers. A collision across them — however
unlikely — must not suppress a real transaction.

### 3.2 Observation — `UX_Transactions_Organization_EvidenceFingerprint`

Unique on `(OrganizationId, EvidenceFingerprint)`.

Answers: *"has this real-world event already been recorded?"* Covers the same SMS being read
by two devices, or re-read after a reinstall.

**Why organization and not device:** two devices in one branch that both witness the same
provider SMS are describing **one** financial event. A device-scoped constraint would record
it twice and double the branch's float movement.

Both indexes are partial (`WHERE ... IS NOT NULL`), so rows legitimately lacking one key do
not collide on `NULL`.

### 3.3 Why the constraint, not the check

The application performs a pre-check, but it is an optimisation. Two concurrent requests can
both read "not found" before either commits, so correctness rests on the database.
`IdempotencyConcurrencyTests` proves this at 2, 10 and 100 simultaneous submissions.

A losing writer catches SQLSTATE `23505`, re-reads the winner, and returns it. The caller
sees a successful idempotent replay, not an error.

---

## 4. Evidence fingerprint

`SHA-256` over a version-prefixed, newline-delimited canonical field list, lowercase hex.
Version `v1`, stored alongside every fingerprint so schemes never get compared across
versions.

Inputs, in order: `version`, `organizationId`, `provider`, `transactionType`, `amount`,
`providerReference`, `customerPhone`, `occurredAt`.

Canonicalisation:

| Input | Rule | Reason |
|---|---|---|
| provider | Uppercase, whitespace collapsed | Template casing varies. |
| amount | Fixed 4 decimal places | `50`, `50.0`, `50.00` are one amount. |
| reference | Uppercase alphanumerics only | Separators differ between templates. |
| phone | National significant number | `+233…`, `233…`, `0…` are one subscriber. |
| occurredAt | Truncated to the **minute** | Providers report seconds inconsistently. |

**Why not hash the raw SMS:** providers re-word and re-wrap templates without changing the
underlying event. A raw-text hash fingerprints the same event differently after a cosmetic
change, producing duplicate ledger entries. Hashing extracted fields is stable across
template changes.

**Retention consequence:** because the fingerprint derives from fields rather than text, raw
SMS bodies can be purged on schedule (`RawMessagePurgedAtUtc`) while duplicate detection
keeps working. A separate `RawHash` detects byte-identical redelivery before parsing.

---

## 5. Evidence versus ledger

```
SMS observed
  → TransactionEvidence          (what was claimed — always recorded)
      → validate, classify, dedupe
          → FinancialTransaction (what was accepted — has a balance effect)
```

`TransactionEvidence` is written for **every** observation, including rejected and duplicate
ones: the record of what was seen and why it was not posted is itself auditable. Many
evidence rows may point at one financial transaction.

A parser finding is never accounting data. `LedgerPolicy.CanPostAutomatically` gates which
classified types may post without human review — `Unknown`, `Reversal` and `Adjustment`
never do.

---

## 6. Client-local sync states

These live only on the device. The server never receives or returns them.

| State | Meaning | Next |
|---|---|---|
| `LOCAL_ONLY` | Written locally, not yet queued | `QUEUED` |
| `QUEUED` | In the outbox, awaiting connectivity | `SYNCING` |
| `SYNCING` | In flight | `SYNCED`, `FAILED`, `REJECTED`, `CONFLICT` |
| `SYNCED` | Server confirmed durable | terminal |
| `FAILED` | Attempt failed, retries remain | `RETRYING` |
| `RETRYING` | Backoff timer running | `SYNCING` |
| `DEAD_LETTER` | Retry budget exhausted; needs a human | `QUEUED` on manual retry |
| `REJECTED` | Server refused it permanently | terminal |
| `CONFLICT` | Server state disagrees | needs a human |

`SYNCING` must be persisted **before** the HTTP call, not after. A crash mid-request would
otherwise leave the row looking un-attempted, and the retry would be indistinguishable from a
first attempt — which is safe only because of the idempotency constraint.

Backoff: 1s, 2s, 4s, 8s … capped at 15 minutes, with jitter, and a default budget of 10
attempts before `DEAD_LETTER`.

**Connectivity is not an error.** A queued transaction is a success from the user's point of
view; the UI must show a sync indicator, never a failure dialog, when the server is simply
unreachable.

---

## 7. Batch sync protocol

`POST /api/v1/sync/transactions` — **architecture groundwork only; not yet implemented.**
The contract below is fixed so the Android client can be built against it.

Server sequence: authenticate → resolve tenant from token → validate branch → validate
session → validate each transaction → check idempotency → validate evidence → persist
atomically → respond per item.

Batch size is configurable (1/10/50/100), default 50, hard cap 100 items and 1 MB.
Unbounded request bodies are a denial-of-service vector.

Each item resolves independently so the client can process partial success:

| Result | Client action |
|---|---|
| `ACCEPTED` | Mark `SYNCED` with the returned server id |
| `DUPLICATE` | Mark `SYNCED` — an earlier attempt already landed |
| `REJECTED` | Mark `REJECTED`, surface for review; do not retry |
| `CONFLICT` | Mark `CONFLICT`, surface for review; do not retry |

`DUPLICATE` is a success, not an error. Treating it as failure is what causes retry storms.

---

## 8. Atomicity

For one accepted transaction these commit together or not at all:

1. `TransactionEvidence`
2. `FinancialTransaction`
3. Ledger projection (`CashBalance`, `FloatBalance`)
4. Audit entry

Enforced with an explicit database transaction. A partial commit would leave a balance no
transaction explains, or a transaction no balance reflects.

**Caveat:** the EF InMemory provider has no transaction support, so the code falls back to a
plain save there. Atomicity is a property of the PostgreSQL path only, which is why the
concurrency tests are PostgreSQL-backed.

---

## 9. Crash recovery

| Scenario | Behaviour |
|---|---|
| App killed during SMS processing | Evidence not yet committed is lost; the SMS is re-read on next scan and fingerprints identically. |
| Reboot during sync | Row is `SYNCING`; retry is idempotent on `ClientTransactionId`. |
| Network drop mid-request | Same as above. |
| **Server committed, response lost** | Retry hits the unique constraint, resolves as `DUPLICATE`, one ledger row. |
| Offline for seven days | Outbox drains in id order on reconnect; no expiry. |
| Device revoked while offline | Refresh fails; queued items stay local and unsent. Nothing is lost, nothing posts. |

Guarantees: no duplicate transaction, no lost transaction, no corrupted session, no
incorrect balance.

---

## 10. Server-side trust rules

Never taken from the client under any circumstances:

- **`OrganizationId`** — from the access token. Not a field on any request model.
- **`AgentId` / `UserId`** — from the token.
- **`Source`** — forced to `Manual` on manual entry, so a client cannot claim its keyed
  entry was verified SMS evidence.
- **`CashDelta` / `FloatDelta`** — resolved by `LedgerPolicy`.
- **`State`** — a client cannot assert `Accepted`.
- **`ServerReceivedAt`** — server clock.

Client-supplied `BranchId`, `DeviceId` and `SessionId` are accepted as *requests* and then
proved to belong to the caller's organization by `ITenantGuard`, which returns 403 — never
404 — so entity ids cannot be probed across tenants.
