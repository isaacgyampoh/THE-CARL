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

## Security design requirements

- Device identity must be cryptographically signed and validated
- Pairing alone is not trusted; device proof is required
- All relevant events include event_id, device_id, sequence, timestamp, and hash/fingerprint
- Hub rejects unknown, revoked, replayed, or malformed events
- Outbox is write-ahead and only acknowledged after durable server confirmation
- Server-side authorization and tenant scoping must be enforced for every API write
- All financial writes must be append-only and idempotent

## Security review gates before production

- Device revocation flow tested
- Replay attack simulation passed
- Cross-tenant access attempt blocked
- Queue persistence and recovery tested under crash conditions
- Local storage encryption reviewed for the target Android versions
- Secrets managed via secure storage and platform APIs
- Audit logs preserved for operational investigations
