# Zazi

Zazi is a production-grade SaaS platform for Mobile Money businesses. It captures evidence of business movements, records expected position, and reconciles operational reality without executing Mobile Money transactions itself.

## Status

This repository is currently greenfield. No application code, infrastructure, or prior implementation exists yet. The project is beginning with the Phase 0 architecture gate and connectivity proof-of-concept plan before any full product build.

## Core architectural truth

- A phone with internet cannot directly read SMS stored on a different offline phone.
- Therefore Zazi must support multiple capture architectures:
  - Mode A: Smart transaction phone with internet
  - Mode B: Smart transaction phone + Hub over local transport
  - Mode C: Feature/YAM fallback or manual capture path
  - Mode D: Manual transaction entry for any device

## Non-goals

- Zazi does not move money
- Zazi does not replace the MoMo transaction itself
- Zazi does not claim remote SMS access where no communication path exists

## Target architecture

- Flutter + SQLite for offline-first mobile client
- ASP.NET Core + PostgreSQL for cloud backend
- Next.js/TypeScript for owner dashboard
- Cloudflare where appropriate
- No Redis/Kubernetes/microservices in the initial path

## Phase 0 outputs

- Repository audit complete
- Phase 0 connectivity prototype plan
- Threat model and tenant model
- Domain and database proposal
- Initial ADR set
- Android/Play SMS compliance gate documentation

## Documentation index

Running it:

- [docs/PRODUCTION.md](docs/PRODUCTION.md) — deploying to a real server, with TLS
- [docs/PILOT.md](docs/PILOT.md) — running a trial from a laptop
- [docs/TESTING.md](docs/TESTING.md) — the test database and how to run the suites
- [docs/MIGRATION_SAFETY.md](docs/MIGRATION_SAFETY.md) — applying schema changes

Design:

- [docs/phase0-connectivity-prototype.md](docs/phase0-connectivity-prototype.md)
- [docs/threat-model.md](docs/threat-model.md)
- [docs/domain-model.md](docs/domain-model.md)
- [docs/adr/ADR-001-core-architecture.md](docs/adr/ADR-001-core-architecture.md)
- [docs/adr/ADR-002-multi-tenancy.md](docs/adr/ADR-002-multi-tenancy.md)
- [docs/adr/ADR-003-financial-ledger.md](docs/adr/ADR-003-financial-ledger.md)
- [docs/adr/ADR-004-offline-first.md](docs/adr/ADR-004-offline-first.md)
- [docs/adr/ADR-005-local-transport.md](docs/adr/ADR-005-local-transport.md)
- [docs/adr/ADR-006-sms-capture.md](docs/adr/ADR-006-sms-capture.md)
- [docs/adr/ADR-007-android-play-compliance.md](docs/adr/ADR-007-android-play-compliance.md)
- [docs/adr/ADR-008-feature-yam-fallback.md](docs/adr/ADR-008-feature-yam-fallback.md)
- [docs/adr/ADR-009-device-identity-security.md](docs/adr/ADR-009-device-identity-security.md)
- [docs/adr/ADR-010-scaling.md](docs/adr/ADR-010-scaling.md)
- [docs/adr/ADR-011-disaster-recovery.md](docs/adr/ADR-011-disaster-recovery.md)
- [docs/adr/ADR-012-privacy-retention.md](docs/adr/ADR-012-privacy-retention.md)

## Immediate next step

The next step is the connectivity proof-of-concept build: transaction device -> local transport -> Hub -> cloud with idempotent queueing and reconciliation-safe sync semantics.
