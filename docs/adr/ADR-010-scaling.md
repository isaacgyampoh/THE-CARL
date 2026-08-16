# ADR-010: Scaling

- Status: Accepted
- Date: 2026-08-15

## Context

The project must not over-engineer before proving actual workload and traffic patterns.

## Decision

Start with a simple modular monolith with measured validation. Only add further infrastructure after measuring:
- API load
- DB load
- connection counts
- latency
- transaction/sec
- sync events/sec
- storage growth
- report workload

Scaling thresholds:
- 0–10K: simple modular monolith
- 10K–100K: optimization and indexes
- 100K–500K: specialized workers only if justified
- 500K–2M+: stronger load balancing and database scaling

## Consequences

- Simpler initial setup and easier debugging
- Scaling justified by real evidence rather than speculation
- Future optimization can be targeted instead of premature
