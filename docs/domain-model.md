# Domain and Database Proposal

## Core domain concepts

### Tenant, business, and branch
- Business
- Branch
- Device
- User / role
- Device role: HUB | TRANSACTION_DEVICE | OWNER_DEVICE | MANAGER_DEVICE

### Financial objects
- Transaction event
- Canonical transaction
- Ledger entry
- Reversal / compensating entry
- Cash float and reconciliation view

### Operational objects
- Outbox item
- Sync attempt
- Audit record
- Session state
- Device health record

## Device state

- PENDING
- ACTIVE
- OFFLINE
- REVOKED
- QUARANTINED
- REQUIRES_REAUTH

## Local SQLite model

- devices
- transactions
- evidence_metadata
- outbox
- sync_attempts
- session_state
- local_projections

## Cloud PostgreSQL model

- canonical_business_data
- transactions
- ledger
- audit
- branches
- memberships
- devices
- reports

## Event integrity contract

Every event should carry at least:
- event_id
- device_id
- sequence
- occurred_at
- source
- network
- type
- amount
- currency
- provider_reference
- customer_identifier_if_present
- parser_version
- fingerprint
- confidence

## Ledger rules

- Use exact decimal money representation
- Append-only ledger model
- No silent balance edits
- Atomic posting
- Idempotency guard on canonical transaction creation
- Reversal and compensating entries only through controlled operations

## Sync rules

- Never delete outbox records before durable server acknowledgement
- Retry sync using bounded attempts and audit trail
- Reconcile queue state on startup
- Reject cross-business events during ingestion
