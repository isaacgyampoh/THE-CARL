# ADR-008: Feature/YAM fallback

- Status: Accepted
- Date: 2026-08-15

## Context

A true feature phone cannot be assumed to support remote SMS reading through software alone. The architecture must work even when the transaction device cannot run a companion app.

## Decision

Use the fallback priority order:
1. Manual transaction entry
2. Supported carrier/provider integration
3. Approved external SMS gateway/hardware capture
4. Future Zazi hardware gateway

Every external capture option requires an ADR and feasibility review before being treated as viable.

## Consequences

- The product stays honest about technical limitations
- Manual mode remains a supported and safe fallback
- False claims of remote feature-phone capture are avoided
