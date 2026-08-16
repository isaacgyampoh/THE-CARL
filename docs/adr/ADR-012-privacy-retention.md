# ADR-012: Privacy and retention

- Status: Accepted
- Date: 2026-08-15

## Context

THE CARL handles business financial events and may process event metadata and SMS-derived evidence. Privacy obligations and data minimization must be explicit.

## Decision

- Collect only data required for transaction validation and reconciliation
- Avoid raw SMS logging in normal operational logs
- Store only needed identifiers and metadata
- Define retention windows and secure deletion policies
- Provide clear consent and privacy notice language
- Limit support access to minimal necessary personnel and logs

## Consequences

- Privacy controls are part of the architecture rather than a last-minute patch
- Sensitive data access is reduced and easier to audit
- Compliance and support operations are safer and clearer
