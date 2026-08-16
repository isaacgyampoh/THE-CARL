# ADR-006: SMS capture

- Status: Accepted for design gate; implementation deferred
- Date: 2026-08-15

## Context

SMS-based transaction capture can be valuable, but it is a policy-sensitive, device-sensitive, and provider-sensitive subsystem. It must not be implemented as UI logic.

## Decision

Create SMS capture as a versioned subsystem with:
- provider identification
- normalization
- parser registry
- parser versioning
- extraction
- validation
- fingerprinting
- duplicate detection
- confidence scoring
- transaction candidate generation

Only implement restricted SMS permissions after policy, device compatibility, and distribution eligibility are validated.

## Consequences

- SMS logic remains cleanly separated from the UI and business workflow
- Policy and code review can be done intentionally and safely
- The system can fall back to manual mode without undermining compliance
