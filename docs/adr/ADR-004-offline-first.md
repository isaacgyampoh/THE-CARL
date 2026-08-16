# ADR-004: Offline-first

- Status: Accepted
- Date: 2026-08-15

## Context

Transaction devices and Hubs may work with no internet access for long periods. Business recording must not be lost simply because network availability changes.

## Decision

Local SQLite persistence is mandatory on mobile devices and Hubs. Outbox-based queueing is the standard mechanism for all outbound events. Durable acknowledgement must happen before the queue entry is treated as safe to delete.

## Consequences

- Event capture continues even when the network is unavailable
- Recovery is deterministic after restarts and reconnects
- Sync logic must include idempotency and retry-safe behavior
