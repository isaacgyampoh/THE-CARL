# ADR-009: Device identity and security

- Status: Accepted
- Date: 2026-08-15

## Context

Local transport cannot be trusted based on pairing alone. A malicious or stale device may still try to send transactions.

## Decision

Each device must register with a cryptographic identity and a business/branch scope. Pairing must include explicit user confirmation and secure channel setup. Events must include device identity, timestamp, sequence, and integrity metadata. Unknown, revoked, or replayed events are rejected.

## Consequences

- Device trust is verified before the Hub accepts transactions
- Replay attacks are strongly reduced
- Device revocation becomes meaningful and enforceable
