from __future__ import annotations

from dataclasses import dataclass
from typing import Dict, List, Optional

from prototype.api_simulator import ApiSimulator
from prototype.contracts import DeviceRegistration, EventContract, SignedSyncRequest, build_signed_request
from prototype.local_store import OfflineStore
from prototype.models import Device, DeviceRegistry, Event, HubQueue, CloudLedger, SyncCoordinator, TransactionDeviceClient
from prototype.recovery_simulator import RecoverySimulator


@dataclass
class EndToEndResult:
    device_event: Event
    signed_request: SignedSyncRequest
    api_status: str
    queue_after_sync: int
    duplicate_detected: bool
    recovered_after_restart: Optional[List[str]]


class EndToEndFlow:
    def __init__(self) -> None:
        self.device_registry = DeviceRegistry()
        self.device = Device(
            device_id="dev-e2e",
            business_id="biz-e2e",
            branch_id="branch-e2e",
            role="TRANSACTION_DEVICE",
            status="ACTIVE",
            revoked=False,
        )
        self.device_registry.register(self.device)
        self.hub_queue = HubQueue()
        self.ledger = CloudLedger()
        self.coordinator = SyncCoordinator(self.device_registry, self.hub_queue, self.ledger)
        self.client = TransactionDeviceClient(self.device)
        self.api = ApiSimulator()
        self.store = OfflineStore(":memory:")
        self.store.upsert_device(self.device.device_id, self.device.business_id, self.device.branch_id, self.device.role)
        self.recovery = RecoverySimulator(self.store)

    def run(self) -> EndToEndResult:
        device_event = self.client.create_event(1, "50.00", "DEPOSIT", "ref-e2e")
        self.store.save_event({
            "event_id": device_event.event_id,
            "device_id": device_event.device_id,
            "business_id": device_event.business_id,
            "branch_id": device_event.branch_id,
            "sequence": device_event.sequence,
            "occurred_at": device_event.occurred_at.isoformat(),
            "source": device_event.source,
            "network": device_event.network,
            "event_type": device_event.event_type,
            "amount": device_event.amount,
            "currency": device_event.currency,
            "provider_reference": device_event.provider_reference,
            "parser_version": device_event.parser_version,
            "fingerprint": device_event.fingerprint,
            "confidence": device_event.confidence,
        })
        self.store.enqueue_outbox({
            "event_id": device_event.event_id,
            "device_id": device_event.device_id,
            "business_id": device_event.business_id,
            "branch_id": device_event.branch_id,
            "sequence": device_event.sequence,
        })

        signed_registration = DeviceRegistration(
            business_id=self.device.business_id,
            branch_id=self.device.branch_id,
            device_id=self.device.device_id,
            role=self.device.role,
            credential_public_key=f"device-key-{self.device.device_id}",
            app_version="1.0.0",
            os_version="Android 14",
        )

        event_contract = EventContract(
            event_id=device_event.event_id,
            device_id=device_event.device_id,
            business_id=device_event.business_id,
            branch_id=device_event.branch_id,
            sequence=device_event.sequence,
            occurred_at=device_event.occurred_at.isoformat(),
            source=device_event.source,
            network=device_event.network,
            type=device_event.event_type,
            amount=device_event.amount,
            currency=device_event.currency,
            provider_reference=device_event.provider_reference,
            parser_version=device_event.parser_version,
            fingerprint=device_event.fingerprint,
            confidence=device_event.confidence,
        )
        signed_request = build_signed_request(event_contract, signed_registration)
        api_response = self.api.receive(signed_request)

        duplicate_detected = api_response.status == "DUPLICATE"
        queue_after_sync = len(self.hub_queue.events_for(self.device.device_id))

        recovery_result = self.recovery.restart_and_recover()
        return EndToEndResult(
            device_event=device_event,
            signed_request=signed_request,
            api_status=api_response.status,
            queue_after_sync=queue_after_sync,
            duplicate_detected=duplicate_detected,
            recovered_after_restart=recovery_result.replayed_event_ids,
        )
