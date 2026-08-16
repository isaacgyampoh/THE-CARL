# ADR-003: Financial ledger

- Status: Accepted
- Date: 2026-08-15

## Context

Financial records must be trustworthy, auditable, and safe under partial failures or retries.

## Decision

Use an append-only ledger with exact decimal money representation. Canonical posting always occurs server-side. Client devices do not control money state. Reversals are handled via compensating ledger entries rather than silent balance edits.

## Consequences

- Financial integrity is enforced centrally
- Duplicate events do not create duplicate postings
- Reconciliation and audit become simpler and safer
- The system is more robust under sychronization retries and offline recovery
