# Phase 0 Connectivity Proof-of-Concept

## Objective

Prove that a transaction device without internet can still safely capture transaction evidence and move it through a Hub into the cloud without losing, duplicating, or authorizing wrong records.

## Architecture

### Mode B flow

1. Transaction device receives a supported transaction signal (manual entry or compliant capture path)
2. Transaction device persists to local SQLite
3. Transaction device signs the event with device identity
4. Local transport delivers to Hub (Bluetooth / Wi-Fi Direct / hotspot / LAN)
5. Hub validates device, tenant scope, and event fingerprint
6. Hub stores event to SQLite queue
7. Hub waits for internet connectivity
8. Hub authenticates and syncs to ASP.NET Core API
9. API validates idempotency and authorization
10. Canonical transaction is posted once
11. API acknowledges durable sync to Hub
12. Hub marks queue item as synced and removes only after durable ACK

## Non-functional constraints

- No duplicate ledger postings
- No cross-business device acceptance
- No silent financial modification
- Local queue must survive crash, reboot, and network loss
- Manual mode must remain functional even when SMS access is not allowed

## Local transport abstraction

```text
interface LocalTransport {
  discover()
  pair()
  authenticatePeer()
  sendEvent()
  receiveEvent()
  health()
  disconnect()
}
```

Prototype implementations:
- Bluetooth nearby devices
- Wi-Fi Direct peer-to-peer
- Local-only Wi-Fi hotspot
- Ordinary LAN if available

## Required proof points

- Two transaction devices can send events to one Hub with no internet on the devices
- Five devices can burst events to one Hub without event loss or double posting
- App crash or reboot does not lose queued events
- Revoke device event flow is rejected
- Duplicate or replayed events are rejected without ledger effect
- Out-of-order events are handled deterministically
- Timeout after accepted request does not create duplicate ledger entries

## Test matrix

### Connectivity
- Device offline + Hub online + cloud online
- Device offline + Hub offline
- Hub internet reconnect after outage

### Integrity
- Duplicate event
- Late event
- Out-of-order event
- Clock mismatch
- Sync timeout after accepted request

### Recovery
- App kill during sync
- Reboot during queue flush
- Recovery after partial write

### Security
- Unknown device rejected
- Revoked device rejected
- Fake event rejected
- Cross-business event rejected
- Malicious peer rejected

## Acceptance criteria

- Zero lost events after crash/restart
- Zero duplicate ledger postings
- Zero unauthorized device acceptance
- Zero cross-business events
- Deterministic retry behavior
- Documented limitations

## Implementation order

1. Shared contracts and event schema
2. Local SQLite outbox and queue model
3. Device identity and signed event payload
4. Local transport abstraction and Bluetooth prototype
5. Hub queueing and validation
6. API sync endpoint with idempotency checks
7. Recovery tests and concurrency tests
8. Security validation and device revocation

## Guardrails

- Do not implement SMS permissions before policy review is complete
- Keep manual offline entry available as a supported fallback
- No direct SMS remote-read claims without a proven communication path
- No insecure workaround that violates policy or weakens tenant trust
