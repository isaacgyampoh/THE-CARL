# Threat Model

## Assets

- Device identity and credentials
- Transaction event integrity
- Ledger correctness and idempotency
- Tenant isolation
- Local queue durability
- Privacy of customer and business data

## Trust boundaries

- Mobile transaction device
- Hub device
- Cloud API and PostgreSQL
- Local transport channel
- External SMS or gateway path

## Threats

- Device spoofing or fake event injection
- Replay and duplicate posting
- Cross-tenant event submission
- Revoked device continuing to submit data
- Queue loss when app crashes mid-write
- Tampering of local device pairing data
- Unauthorized access to sensitive financial records
- Privacy leakage from raw SMS or customer metadata
- Guessing or replaying a worker activation code
- A leaked code being redeemed into a branch or role it was not issued for

## Security design requirements

- Device identity must be cryptographically signed and validated
- Pairing alone is not trusted; device proof is required
- All relevant events include event_id, device_id, sequence, timestamp, and hash/fingerprint
- Hub rejects unknown, revoked, replayed, or malformed events
- Outbox is write-ahead and only acknowledged after durable server confirmation
- Server-side authorization and tenant scoping must be enforced for every API write
- All financial writes must be append-only and idempotent

### Worker activation

`POST /devices/activate` is the only anonymous endpoint that can produce a session, so its
controls are stated separately rather than left implied.

- The code is the sole bootstrap credential and must never become a standing one: single use,
  expiring, and revocable. It is stored as a SHA-256 hash and the plaintext is returned once.
- Scope — organization, branch, role and worker — is fixed when the code is issued and read
  from the code at redemption. The client supplies only facts about its own hardware, and
  accepting any scoped value from it would let a handset choose its own tenant.
- The code's branch and the intended worker's branch must match. They are separate fields
  that are individually valid, and a mismatch produces a worker whose token claims one branch
  while the device recording their transactions sits in another. Enforced when the code is
  issued and again when it is redeemed, because codes predating the rule still exist.
- Guessing is bounded from two directions: a per-code failed-attempt counter, which does
  nothing against an attacker trying many different codes, and a per-IP rate limit, which
  does. Both are needed; neither is sufficient alone.
- Every rejection is identical. Invalid, expired, revoked, spent, attempt-limited,
  worker-disabled and branch-mismatched share one status and one body, so the endpoint cannot
  be used to discover which codes exist.
- Losing the authenticated-caller requirement on this path is a real reduction against the
  enrolment endpoint it sits beside, accepted because requiring every worker to hold an
  account is the obstacle activation exists to remove. Recorded here rather than glossed.

## Security review gates before production

- Device revocation flow tested
- Replay attack simulation passed
- Cross-tenant access attempt blocked
- Queue persistence and recovery tested under crash conditions
- Local storage encryption reviewed for the target Android versions
- Secrets managed via secure storage and platform APIs
- Audit logs preserved for operational investigations
