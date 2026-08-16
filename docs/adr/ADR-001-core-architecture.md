# ADR-001: Core architecture

- Status: Accepted
- Date: 2026-08-15

## Context

THE CARL must support offline-first mobile workflows, local transport, and cloud reconciliation without starting with a large infrastructure footprint. The repository is greenfield and no prior implementation exists.

## Decision

Use a simple modular architecture with clear boundaries:
- Flutter mobile apps for Android and offline client logic
- SQLite for local persistence and queueing
- ASP.NET Core backend for API and business services
- PostgreSQL for canonical data and ledger
- Next.js for owner dashboard and management workflows
- Cloudflare for edge protections where needed

Do not add Redis, Kubernetes, or microservices at the initial stage.

## Consequences

- Easier to validate connectivity and integrity in the proof-of-concept
- Clearer separation between local transport, sync, and ledger logic
- Lower operational complexity while the solution is still being validated
- Scaling and decomposition can be justified later by measured loads
