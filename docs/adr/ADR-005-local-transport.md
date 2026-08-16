# ADR-005: Local transport

- Status: Accepted
- Date: 2026-08-15

## Context

A transaction device without internet still needs to reach a Hub. Multiple Android transport options are available, and they behave differently across OEMs and Android versions.

## Decision

Model a transport abstraction:
- discover()
- pair()
- authenticatePeer()
- sendEvent()
- receiveEvent()
- health()
- disconnect()

Prototype and validate on real devices:
- Bluetooth
- Wi-Fi Direct
- Local-only hotspot / LAN

## Consequences

- Business logic is decoupled from transport technology
- Real device validation takes precedence over assumptions
- The project can fall back if one path is unreliable on a given hardware profile
