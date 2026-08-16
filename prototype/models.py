from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from hashlib import sha256
from typing import Dict, List, Optional, Set


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass(frozen=True)
class Device:
    device_id: str
    business_id: str
    branch_id: str
    role: str
    status: str = "ACTIVE"
    revoked: bool = False


@dataclass(frozen=True)
class Event:
    event_id: str
    device_id: str
    business_id: str
    branch_id: str
    sequence: int
    occurred_at: datetime
    source: str
    network: str
    event_type: str
    amount: str
    currency: str
    provider_reference: str
    parser_version: str = "v1"
    confidence: float = 1.0
    customer_identifier_if_present: Optional[str] = None

    @property
    def fingerprint(self) -> str:
        canonical = "|".join(
            [
                self.event_id,
                self.device_id,
                self.business_id,
                self.branch_id,
                str(self.sequence),
                self.occurred_at.isoformat(),
                self.source,
                self.network,
                self.event_type,
                self.amount,
                self.currency,
                self.provider_reference,
                self.parser_version,
                str(self.confidence),
                self.customer_identifier_if_present or "",
            ]
        )
        return sha256(canonical.encode("utf-8")).hexdigest()


@dataclass
class OutboxEntry:
    event_id: str
    device_id: str
    business_id: str
    branch_id: str
    sequence: int
    payload: Event
    status: str = "QUEUED"
    attempts: int = 0
    last_error: Optional[str] = None


@dataclass
class DeviceRegistry:
    devices: Dict[str, Device] = field(default_factory=dict)

    def register(self, device: Device) -> None:
        self.devices[device.device_id] = device

    def get(self, device_id: str) -> Optional[Device]:
        return self.devices.get(device_id)

    def is_valid(self, event: Event) -> bool:
        device = self.get(event.device_id)
        if device is None:
            return False
        if device.revoked or device.status != "ACTIVE":
            return False
        if device.business_id != event.business_id or device.branch_id != event.branch_id:
            return False
        return True


class HubQueue:
    def __init__(self) -> None:
        self._events: Dict[str, Event] = {}
        self._by_device: Dict[str, List[Event]] = {}

    def enqueue(self, event: Event) -> None:
        self._events[event.event_id] = event
        self._by_device.setdefault(event.device_id, []).append(event)

    def contains(self, event_id: str) -> bool:
        return event_id in self._events

    def events_for(self, device_id: str) -> List[Event]:
        return list(self._by_device.get(device_id, []))


class CloudLedger:
    def __init__(self) -> None:
        self.posted_event_ids: Set[str] = set()

    def post(self, event: Event) -> str:
        if event.event_id in self.posted_event_ids:
            return "DUPLICATE"
        self.posted_event_ids.add(event.event_id)
        return "POSTED"


class SyncCoordinator:
    def __init__(self, registry: DeviceRegistry, hub_queue: HubQueue, ledger: CloudLedger) -> None:
        self.registry = registry
        self.hub_queue = hub_queue
        self.ledger = ledger

    def validate_event(self, event: Event) -> bool:
        return self.registry.is_valid(event)

    def accept_event(self, event: Event) -> str:
        if not self.validate_event(event):
            return "REJECTED"
        if self.hub_queue.contains(event.event_id):
            return "DUPLICATE"
        self.hub_queue.enqueue(event)
        status = self.ledger.post(event)
        if status == "POSTED":
            return "POSTED"
        return "DUPLICATE"


class TransactionDeviceClient:
    def __init__(self, device: Device) -> None:
        self.device = device
        self.outbox: List[OutboxEntry] = []

    def create_event(self, sequence: int, amount: str, event_type: str, provider_reference: str, network: str = "MTN") -> Event:
        event = Event(
            event_id=f"evt-{self.device.device_id}-{sequence}",
            device_id=self.device.device_id,
            business_id=self.device.business_id,
            branch_id=self.device.branch_id,
            sequence=sequence,
            occurred_at=utc_now(),
            source="transaction-device",
            network=network,
            event_type=event_type,
            amount=amount,
            currency="GHS",
            provider_reference=provider_reference,
            parser_version="v1",
            confidence=1.0,
        )
        self.outbox.append(OutboxEntry(
            event_id=event.event_id,
            device_id=event.device_id,
            business_id=event.business_id,
            branch_id=event.branch_id,
            sequence=event.sequence,
            payload=event,
        ))
        return event

    def flush_outbox(self, coordinator: SyncCoordinator) -> List[str]:
        results: List[str] = []
        for entry in self.outbox:
            if entry.status == "QUEUED":
                result = coordinator.accept_event(entry.payload)
                entry.status = "SENT" if result == "POSTED" else "DUPLICATE"
                results.append(result)
        return results
