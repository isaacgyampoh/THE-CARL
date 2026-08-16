# THE CARL Architecture

## Overview

THE CARL is a production-grade mobile money business operations platform. It manages organization onboarding, branch operations, session tracking, transaction capture, reconciliation, and multi-device coordination under a multi-tenant SaaS model.

## System boundaries

- Domain: enterprise entities and aggregate rules
- Application: business services and DTO contracts
- Infrastructure: EF Core data access and persistence adapters
- API: REST endpoints for registration, sessions, transactions, and branch operations

## Core principles

- Clean architecture with dependency inversion
- Multi-tenant isolation enforced in service and persistence layers
- Decimal money representation for all financial values
- Offline-first design for transaction capture and synchronization
- Manual transaction workflows remain supported when automatic capture is unavailable
- Security and auditability are built into the first slice

## Current implementation status

This repository now contains the initial production backend foundation and first business slice for:

- organizations
- branches
- device registration
- sessions
- manual transactions
- reconciliation
- audit logging

The data layer is configured as PostgreSQL-ready via Npgsql and EF Core migrations, while the application remains able to fall back to the in-memory provider for local development validation when a connection string is not supplied.

## PostgreSQL and migration baseline

The runtime configuration now reads a connection string from the API configuration and sets up the application DbContext with Npgsql when a production connection string is present. The initial migration is stored under `src/TheCarl.Infrastructure/Migrations` and includes the current production domain model for organizations, branches, users, sessions, transactions, audit entries, floats, reconciliation records, and the sync-queue model used for offline-first processing.

## Android offline architecture

The platform now includes a real offline-first queue model for local device event persistence. Pending events are captured in durable application storage and can be processed when connectivity returns. This supports the mobile-money operating model required by THE CARL: capture locally, queue, synchronize, validate, and then reconcile without losing a transaction because the internet disappears.

## SMS capture and parser architecture

The current production foundation includes a bounded SMS processing component that accepts inbound transaction messages, normalizes them, identifies likely network/provider, extracts amount/reference/customer details, applies duplicate detection, and records the dispatch in the offline sync queue. This establishes the architecture required for supported mobile-money message ingestion without claiming impossible remote device access.

## Authentication and tenant isolation architecture

The current implementation intentionally establishes the security boundaries before full identity issuance. The application layer exposes service contracts and DTOs that keep tenant and branch scoping explicit. Access is designed around:

- organization ownership and branch membership
- user/persona scoping by organization and branch
- device registration under a tenant branch
- session ownership validation
- audit append-only tracking for activity and reconciliation events

A full JWT refresh-token implementation should be layered on top of this tenant-aware foundation in the next phase, but the repository already enforces the production architecture boundaries required for safe multi-tenant access control.

## Financial model and reconciliation

All transactional values remain decimal-based to avoid floating-point errors. Manual transaction creation records the event, recalculates expected session cash, and creates a reconciliation record that stores the actual cash versus expected cash spread. This provides an auditable ledger foundation before the SMS capture and sync layers are introduced.

## Float alerts and owner dashboard foundation

The platform now includes organization-level float alert thresholds and dashboard summaries. Alert thresholds can be configured per organization or branch for each network, and the evaluator raises critical or warning alerts when branch floats fall below configured levels. The dashboard service aggregates branch count, daily transaction volume, cash position, network float total, active sessions, active devices, and reconciliation variance so the owner dashboard has a real operational baseline without requiring a fake analytics layer.


---

# Core rule: four separate concepts

THE CARL keeps four things apart that are easy to conflate. Folding any two together creates
two sources of truth that drift, and in a financial system drift means wrong money.

| Concept | Answers | Type | Authority |
|---|---|---|---|
| **DeviceType** | What the device *is* | `DeviceType` enum | Platform / form factor |
| **DeviceRole** | What the device *does* for the business | `DeviceRole` enum | Business responsibility |
| **PlatformCapability** | What the device *can technically do* | `PlatformCapability` + `PlatformCapabilityPolicy` | Server-side, per DeviceType |
| **Authorization** | What the principal *may do* | `CarlPolicies` / `CarlRoles` / `ITenantGuard` | Per authenticated caller |
| **EvidenceSourceType** | How evidence was *acquired* | `EvidenceSourceType` + `ITransactionEvidenceSource` | Acquisition method |

## Why DeviceType excludes role

Values like `AndroidAgent` or `AndroidOwner` were deliberately rejected. They fold a business
role into a platform identity and duplicate `DeviceRole.OwnerDevice` — two fields that can
disagree about whether a device belongs to an owner, with nothing to reconcile them.

An owner's Android handset is `DeviceType.AndroidPhone` + `DeviceRole.OwnerDevice`.

```
DeviceType.AndroidPhone   ─┐
                           ├─► capabilities  (PlatformCapabilityPolicy)
DeviceRole.OwnerDevice    ─┘
        │
        └─────────────────────► permissions   (authorization policies)
```

## Capabilities are not permissions

`PlatformCapability.SmsCapture` says the operating system *can* deliver incoming messages to
an application. It says nothing about whether this user may record transactions — that
remains the authorization policies' decision. A revoked device is granted **no** capabilities
at all, because capabilities describe what a *trusted* device may do.

## Platform reality

| Platform | SMS capture | Manual capture | Offline store | Guaranteed background |
|---|---|---|---|---|
| Android phone | ✅ | ✅ | ✅ | ✅ |
| Android tablet | ✖ (often no SIM) | ✅ | ✅ | ✅ |
| iPhone / iPad | ✖ **never** | ✅ | ✅ | ✖ OS-scheduled |
| Web browser | ✖ | ✅ | ✖ | ✖ |
| GSM gateway | ✅ | ✖ (no operator) | ✅ | ✅ |
| Other | ✖ | ✅ | ✖ | ✖ |

**iOS grants third-party applications no access to arbitrary incoming SMS.** There is no
entitlement and no supported workaround. `SmsCapture` is therefore absent from both iOS
entries and must stay absent. iOS is not a degraded Android — it captures manually and
receives every other capability its hardware supports.

Clients **query** `GET /devices/me` for capabilities. They never hardcode
`if (platform == "Android")`. That is what allows the server to correct a platform assumption
without shipping a new app.

## Evidence flow — the one path money may take

```
Evidence source  (Android SMS · manual entry · import · future gateway)
      ↓
TransactionEvidence          what was observed — always recorded, even when rejected
      ↓
SmsEvidencePolicy            is it complete enough to trust?
      ↓
FinancialTransaction         what was accepted
      ↓
LedgerPolicy                 the only authority for direction
      ↓
Balance projection → Reconciliation
```

No UI, controller or evidence source may bypass this. `ITransactionEvidenceSource`
implementations produce evidence and nothing else — they never construct a
`FinancialTransaction` directly and never touch a balance.

## Legacy `Device.Platform`

The free-text `Platform` string predates `DeviceType`, was never validated, and is **retained
for backward compatibility**. `DeviceType` is derived from it at enrolment and was backfilled
from it by `Phase1DeviceTypeAndCapabilities`. New code reads `DeviceType`; `Platform` is
deprecated but not removed, because existing clients still read it.
