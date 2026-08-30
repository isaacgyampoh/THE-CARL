# Batch Synchronisation

`POST /api/v1/sync/transactions`

Accepts transactions a device captured while offline. Implements
[OFFLINE_TRANSACTION_CONTRACT.md](OFFLINE_TRANSACTION_CONTRACT.md).

Zazi never initiates, authorises or moves money. This endpoint records evidence of
transactions that already happened on a provider's network.

---

## Authentication and authorization

Bearer token. Policy `sync.submit` — `PLATFORM_ADMIN`, `OWNER`, `ORGANIZATION_ADMIN`,
`BRANCH_MANAGER`, `SUPERVISOR`, `AGENT`. `AUDITOR` is excluded: auditors are read-only.

Rate limited by the `tenant` policy — 300 requests/minute per authenticated user. The limit
is on **requests**, not transactions, so a device returning from 48 hours offline can drain
its backlog in batches of 100 without being throttled. Batch size is the second lever.

---

## Request

```json
{
  "transactions": [
    {
      "clientTransactionId": "CTX-9f3a1c07-01HQ8Z7K3M4N5P6Q7R8S9T0V1W",
      "transactionType": 0,
      "amount": 500.00,
      "provider": "MTN",
      "transactionTimestamp": "2026-08-15T09:30:00Z",
      "deviceReceivedAt": "2026-08-15T09:30:02Z",
      "branchId": "…", "deviceId": "…", "sessionId": "…",
      "currency": "GHS",
      "customerPhone": "0241234567",
      "transactionReference": "ABC123",
      "parserVersion": "mtn-v1.2",
      "sourceType": 0
    }
  ]
}
```

**There is no `organizationId` field.** The tenant comes from the access token, so there is
nothing for a client to tamper with.

`transactionType`: `0` CashIn · `1` CashOut · `2` Transfer · `3` Reversal · `4` Commission ·
`5` Adjustment · `6` Unknown.
`sourceType`: `0` AndroidSms · `1` ManualEntry · `2` GsmGateway · `3` Relay · `4` ProviderApi.

Conditional fields: `parserVersion` is required when `sourceType` is `AndroidSms`;
`reversesTransactionId` when type is `Reversal`; `adjustmentCashDelta`/`adjustmentFloatDelta`
plus `correctionReason` when type is `Adjustment`.

### Batch size

Hard maximum **100**, configurable via `Sync:MaxBatchSize`. A larger batch is **refused
whole** with `413`, never truncated — a client that believed the remainder was accepted
would silently lose transactions. The response carries the limit so the client can re-chunk:

```json
{ "status": 413, "title": "The synchronisation batch is too large.",
  "maxBatchSize": 100, "submitted": 250, "correlationId": "…" }
```

---

## Response

`200 OK` even when some items fail. **Callers must read per-item results rather than
inferring outcome from the HTTP status.**

```json
{
  "batchId": "…",
  "submitted": 100, "accepted": 97, "duplicate": 2, "rejected": 1, "conflict": 0,
  "serverReceivedAtUtc": "2026-08-15T09:31:00Z",
  "results": [
    { "clientTransactionId": "CTX-…", "status": "Accepted",  "transactionId": "…", "reasonCode": null },
    { "clientTransactionId": "CTX-…", "status": "Duplicate", "transactionId": "…", "reasonCode": "DUPLICATE_CLIENT_TRANSACTION_ID" },
    { "clientTransactionId": "CTX-…", "status": "Rejected",  "transactionId": null, "reasonCode": "INVALID_AMOUNT" }
  ]
}
```

| Status | Meaning | Client action |
|---|---|---|
| `Accepted` | Posted to the ledger | Mark `SYNCED` with `transactionId` |
| `Duplicate` | Already recorded — **a success** | Mark `SYNCED`; stop retrying |
| `Rejected` | Permanently invalid | Mark `REJECTED`; surface for review; never retry |
| `Conflict` | Server state disagrees | Mark `CONFLICT`; surface for review |

A `Duplicate` carries the original `transactionId`, so a client that lost an earlier response
can still finish reconciling its outbox row.

### Reason codes

Stable API values; never renamed or repurposed.

**Duplicate** — `DUPLICATE_CLIENT_TRANSACTION_ID`, `DUPLICATE_EVIDENCE_FINGERPRINT`

**Rejected (payload)** — `INVALID_CLIENT_TRANSACTION_ID`, `INVALID_AMOUNT`,
`INVALID_TRANSACTION_TYPE`, `INVALID_PROVIDER`, `INVALID_EVIDENCE_FINGERPRINT`,
`INVALID_SOURCE_TYPE`, `MISSING_PARSER_VERSION`

**Rejected (temporal)** — `TIMESTAMP_IN_FUTURE`, `TIMESTAMP_TOO_OLD`

**Rejected (accounting)** — `UNKNOWN_TYPE_REQUIRES_REVIEW`, `REVERSAL_REQUIRES_ORIGINAL`,
`REVERSAL_ORIGINAL_NOT_FOUND`, `REVERSAL_ORIGINAL_NOT_ELIGIBLE`,
`ADJUSTMENT_REQUIRES_EXPLICIT_DELTAS`, `ADJUSTMENT_REQUIRES_REASON`

**Rejected (authorization)** — `BRANCH_NOT_IN_TENANT`, `DEVICE_NOT_IN_TENANT`,
`DEVICE_REVOKED`, `SESSION_NOT_IN_TENANT`, `SESSION_BRANCH_MISMATCH`,
`SESSION_NOT_OPEN_AT_EVENT_TIME`

**Conflict** — `CLIENT_ID_REUSED_WITH_DIFFERENT_PAYLOAD`, `ALREADY_REVERSED`

**Transient (retryable)** — `DATABASE_UNAVAILABLE`, `TIMEOUT`, `TEMPORARY_SERVER_ERROR`

### Status codes

`200` processed (read per-item results) · `400` malformed body · `401` unauthenticated ·
`403` role not permitted · `413` batch too large · `429` rate limited · `500` unexpected.

---

## Idempotency

Two independent boundaries, both organization-scoped and enforced by unique indexes.

| Index | Answers |
|---|---|
| `(OrganizationId, ClientTransactionId)` | Is this the same *submission* again? |
| `(OrganizationId, EvidenceFingerprint)` | Is this the same *real-world event* again? |

The second is what makes two devices witnessing one SMS produce one ledger row. Their
`ClientTransactionId`s differ — they are different submissions — but the fingerprint is
identical, so the second returns `DUPLICATE_EVIDENCE_FINGERPRINT`.

**The fingerprint is always recomputed server-side.** A client-supplied value is validated
for shape but never used for detection: trusting it would let a device evade deduplication by
fabricating one, and would leave clients that omit it unprotected.

**Correctness rests on the database, not on the pre-check.** The service looks for an
existing row first, but two concurrent requests can both find nothing before either commits.
The unique index is the real guarantee; a losing writer catches SQLSTATE `23505`, re-reads the
winner, and returns `Duplicate`.

### Retry safety

Retrying 1, 10 or 100 times is deterministic: the first attempt returns `Accepted`, every
subsequent one returns `Duplicate`, and the balance reflects a single posting. Verified by
`RetryingAHundredTimesRemainsDeterministic`.

The lost-response case — server commits, network drops, client never sees the reply — is the
same path. The retry hits the unique index and resolves as a replay.

---

## Atomicity

Per accepted transaction, committed in one PostgreSQL transaction:

1. `TransactionEvidence` (what was observed)
2. `FinancialTransaction` (what was accepted)
3. Balance projection (`CashBalances`, `FloatBalances`)
4. Audit entry

**Atomicity is per item, not per batch.** Wrapping the whole batch in one transaction would
make partial success impossible: one invalid item would roll back ninety-nine valid ones and
the client could never make progress.

The balance projection is an atomic `INSERT … ON CONFLICT … DO UPDATE` that increments **in
the database**. A read-modify-write from application memory loses updates under concurrency —
two requests both read 100, both write 101, and one transaction's money disappears — and
additionally collides on the balance row's unique index the first time two transactions for a
new branch arrive together. Both failures were observed in testing before this was fixed.

---

## Validation

Every item is validated server-side regardless of any validation the Android client
performed. Client-side validation exists for UX; it is never a reason to trust input.

- **Amount** ≥ ₵0.01, at most 4 decimal places, positive magnitude only — direction comes
  from the type, so a negative amount would silently invert a ledger movement.
- **Unknown** is always rejected. An unclassified transaction reaching the ledger would let a
  parser failure corrupt branch balances.
- **Reversal** must reference an original that exists in the same organization and is
  `Accepted` or `Synced`. Its effect is the exact inverse of the original's.
- **Adjustment** must carry explicit signed deltas and a reason. Direction cannot be inferred
  for a correction.
- **Timestamps** may run up to 5 minutes fast (`Sync:MaxClockSkewAhead`) — device clocks
  drift, and rejecting a real transaction because a handset is a minute fast would lose
  money — and up to 90 days old (`Sync:MaxBacklogAge`), so long offline backlogs still post.

---

## Tenant and device security

| Guarantee | Mechanism |
|---|---|
| No client `organizationId` can override tenancy | The field does not exist on the request model |
| No `branchId` escapes authorization | Every lookup filters on the token's organization; branch-scoped callers are pinned to their own branch |
| No `deviceId` escapes authorization | Device must exist in the tenant **and** belong to the target branch |
| No revoked device can post | Device status checked per batch, not only at login |
| No `userId` impersonation | The agent is the authenticated user; there is no body field for it |
| No cross-tenant posting | Verified by PostgreSQL-backed isolation tests |
| No replay duplication | Unique indexes plus `23505` handling |

Cross-tenant identifiers are rejected as `*_NOT_IN_TENANT` — indistinguishable from "does not
exist", so ids cannot be probed across tenants.

### Session-close rule

A transaction is judged against the session window it claims, **not** against when it happens
to arrive. Work captured at 14:00 in a session that closed at 17:00 still posts when the
device reconnects at 19:00, because the event time falls inside the window. Only an event
time *outside* the window is refused (`SESSION_NOT_OPEN_AT_EVENT_TIME`).

Rejecting late arrivals purely because the session has since closed would discard genuine
financial records for being slow to sync.

### Ordering

Arrival order is irrelevant. Balance projection is additive over stored deltas, so a device
that sends 10:05 before 10:00 produces the same balances as one that sends them in order.
Event time drives financial reporting; server receipt time drives audit ordering.

---

## Observability

Each batch logs one structured record: `BatchId`, `OrganizationId`, `BranchId`, `DeviceId`,
`Submitted`, `Accepted`, `Duplicate`, `Rejected`, `Conflict`, `DurationMs`, `CorrelationId`.
A `SYNC_BATCH_PROCESSED` audit entry records the same counts.

Never logged: SMS bodies, customer phone numbers, passwords, tokens, refresh tokens, MFA
secrets.

---

## Configuration

```jsonc
"Sync": {
  "MaxBatchSize": 100,
  "MaxClockSkewAhead": "00:05:00",
  "MaxBacklogAge": "90.00:00:00"
}
```

Validated at startup; the API refuses to start on an invalid value.

---

## Measured performance

On the development machine (Apple silicon, PostgreSQL 16.2 under Rosetta, single instance):

| Workload | Result |
|---|---|
| 1,000 transactions, 10 batches of 100 | 3,124 ms — **~320 tx/s** |
| 100 concurrent requests, same transaction | 1 posting, balance exact |
| 100 concurrent requests, distinct transactions | 100 postings, balance exact |

Not a tuned benchmark and not a production figure — a repeatable baseline to detect
regression. No optimisation has been applied; per-item transactions are the obvious first
lever if throughput ever matters.


---

## Error categories and retry policy

Every result carries a `category` and an `isRetryable` flag alongside its `reasonCode`.
Clients branch on those, never on `message`, which is written for people and may change.

| Category | Meaning | Retry? |
|---|---|---|
| `Validation` | Payload malformed or breaks an accounting rule | **No** |
| `Authorization` | Caller, device, branch or session not permitted | **No** |
| `Duplicate` | Already recorded — a success | **No** (mark synced) |
| `Conflict` | Server state disagrees; a person must decide | **No** (surface it) |
| `Transient` | Temporary infrastructure failure | Yes, with backoff |
| `System` | Unexpected server fault | Yes, escalate if persistent |

The retry decision lives on the server rather than in each client's head. A client that
retries a permanent validation failure forever burns battery and rate limit and can never
succeed; one that gives up on a transient failure loses a real transaction. An unrecognised
reason code is classified `System`, not `Validation` — an unmapped code means the server
changed and the mapping did not, which should be loud rather than silently permanent.

**Client backoff:** immediate retry → 1s → 2s → 4s → 8s … capped at 15 minutes with jitter,
budget 10 attempts, then `DEAD_LETTER`. Only `Transient` and `System` results consume the
budget.

---

## Conflicts

A conflict is a disagreement the server cannot settle on its own. It is **persisted**, never
merely returned: a conflict reported to a device but forgotten server-side leaves the device
believing something needs attention while the business has no record of it — money in limbo
with nobody accountable.

`SyncConflict` records the organization, branch, device, submitting user, client transaction
id, the related transaction, type, reason code, batch, correlation id, and the submitted
amount and type. It holds identifiers and amounts, not raw evidence, so it does not duplicate
SMS bodies or customer numbers into a second table with its own retention story.

States: `Open` → `UnderReview` → `Resolved` | `Rejected`.

A database check constraint (`CK_SyncConflicts_ResolutionComplete`) requires that a
`Resolved` or `Rejected` conflict carries both `ResolvedAtUtc` and `ResolvedByUserId`.
Without it an audit trail could record that something was settled but not by whom.

Conflict creation is idempotent per `(organization, clientTransactionId, reasonCode)`, so a
client retrying a conflicting submission five times leaves one item on the operator's queue
rather than five.

### Resolution

There is deliberately no "force transaction" action. A financial record is never edited to
make it agree with a client. An operator resolves a conflict by recording a decision, and
corrects money — where correction is warranted — with an `Adjustment` or `Reversal` that
references the original and carries a reason, an actor and a timestamp. The original row is
never mutated.

---

## Reversal safety

**One original, at most one effective reversal.** Enforced by a partial unique index on
`(OrganizationId, ReversesTransactionId)`.

An application check cannot provide this: two concurrent reversals can both read "not yet
reversed" before either commits. Idempotency on `ClientTransactionId` does not provide it
either — two reversals with *different* client ids target the same original, which is exactly
the gap the constraint closes. 100 concurrent reversals of one transaction produce exactly
one reversal and a net balance change of zero.

A second reversal returns `ALREADY_REVERSED` (category `Conflict`) and raises a durable
conflict record. Multiple partial reversals are **not** supported; the constraint is the
deliberate gate that must be revisited if the business ever needs them.

A client retrying *its own* reversal after a lost response is an idempotent replay, not a
second reversal — the idempotency check runs before the reversal check precisely so a
well-behaved outbox is not punished with a spurious conflict.
