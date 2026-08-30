# ADR-002: Multi-tenancy

- Status: Accepted
- Date: 2026-08-15

## Context

Zazi serves multiple businesses and branches. Each device, transaction, and user must remain within the correct tenant boundary.

## Decision

Model tenancy as a hierarchy:
- Business
- Branch
- Device
- User / role

Enforce tenant scoping at the API boundary and in each domain service. Device registration must be tied to a specific business and branch. Transaction ingestion must validate business-level and branch-level scope before posting.

## Consequences

- Tenant isolation becomes explicit and auditable
- Cross-tenant event acceptance is prevented at API and service layers
- Security reviews can test branch and business boundaries deterministically
