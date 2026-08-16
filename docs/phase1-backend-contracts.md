# Phase 1 Backend Sync Contracts

## Objective

Define the signed request and queue model used by a transaction device or Hub when sending events to the cloud API.

## Message contract

### EventContract

Required fields:
- event_id
- device_id
- business_id
- branch_id
- sequence
- occurred_at
- source
- network
- type
- amount
- currency
- provider_reference

Optional fields:
- customer_identifier_if_present
- parser_version
- fingerprint
- confidence

### SignedSyncRequest

The cloud API should accept:
- device_id
- business_id
- branch_id
- sequence
- transaction
- device_public_key
- signature
- request_id
- issued_at

### SyncAck

Accepted response structure:
- request_id
- event_id
- status
- canonical_transaction_id (optional)
- message

## Idempotency rules

- Event_id is the canonical deduplication key for the transaction event
- Sequence is validated per device and business/branch scope
- Fingerprint is recomputed server-side to confirm integrity
- The API blocks duplicate requests and replayed requests

## Outbox exchange model

### OutboxItem

- outbox_id
- business_id
- branch_id
- device_id
- event_id
- sequence
- payload_version
- created_at
- attempt_count
- status

Rules:
- Item remains in outbox until durable server ACK
- Retry on network failure and timeout
- No deletion before ack
- Sequence ordering is validated when replaying pending items

## Minimal API flow

1. Hub or device prepares EventContract
2. Device signs request with its credential public key
3. API verifies device identity and tenant scope
4. API recomputes fingerprint and validates signature
5. API checks idempotency and sequence continuity
6. API posts canonical transaction once
7. API returns SyncAck
8. Hub marks outbox item as synced only after durable ack
