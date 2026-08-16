# ADR-011: Disaster recovery

- Status: Accepted
- Date: 2026-08-15

## Context

A queueing and syncing system must survive outages without data loss or duplicate postings.

## Decision

Define recovery and continuity rules before production:
- durable persistence before queue acknowledgement
- backup and restore plan for PostgreSQL
- retention and replay of outbox events
- recovery procedure for partial syncs
- branches and tenant data restoration procedures

## Consequences

- Recovery is deterministic during outages
- Operational incident response is easier to standardize
- Recovery can be tested and reviewed before production scale
