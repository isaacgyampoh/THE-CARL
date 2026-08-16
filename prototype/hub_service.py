from __future__ import annotations

from dataclasses import dataclass, field
from typing import Dict, List, Optional

from prototype.models import CloudLedger, DeviceRegistry, Event, HubQueue, OutboxEntry


@dataclass(frozen=True)
class SignedEventEnvelope:
    event: Event
    signature: str
    device_public_key: str

    @property
    def validated_signature(self) -> str:
        return self.signature


@dataclass
class SyncResponse:
    status: str
    event_id: str
    message: str = ""


class HubService:
    def __init__(self, registry: DeviceRegistry, queue: HubQueue, ledger: CloudLedger) -> None:
        self.registry = registry
        self.queue = queue
        self.ledger = ledger
        self.received_events: Dict[str, SignedEventEnvelope] = {}
        self.sync_history: List[SyncResponse] = []

    def ingest(self, envelope: SignedEventEnvelope) -> SyncResponse:
        event = envelope.event
        device = self.registry.get(event.device_id)
        if device is None:
            return SyncResponse("REJECTED", event.event_id, "unknown-device")
        if device.revoked or device.status != "ACTIVE":
            return SyncResponse("REJECTED", event.event_id, "revoked-or-inactive-device")
        if device.business_id != event.business_id or device.branch_id != event.branch_id:
            return SyncResponse("REJECTED", event.event_id, "cross-business-or-branch")
        if self.queue.contains(event.event_id):
            return SyncResponse("DUPLICATE", event.event_id, "event-already-queued")
        if envelope.device_public_key != f"device-key-{device.device_id}":
            return SyncResponse("REJECTED", event.event_id, "identity-mismatch")

        self.received_events[event.event_id] = envelope
        self.queue.enqueue(event)
        ledger_status = self.ledger.post(event)
        if ledger_status == "DUPLICATE":
            return SyncResponse("DUPLICATE", event.event_id, "ledger-duplicate")

        response = SyncResponse("POSTED", event.event_id, "accepted")
        self.sync_history.append(response)
        return response

    def sync_pending(self) -> List[SyncResponse]:
        responses: List[SyncResponse] = []
        for event in list(self.queue._events.values()):
            if event.event_id in [response.event_id for response in responses]:
                continue
            status = self.ledger.post(event)
            if status == "DUPLICATE":
                responses.append(SyncResponse("DUPLICATE", event.event_id, "already-posted"))
            else:
                responses.append(SyncResponse("POSTED", event.event_id, "synced"))
        self.sync_history.extend(responses)
        return responses
