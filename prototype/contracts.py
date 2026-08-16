from __future__ import annotations

from dataclasses import dataclass, asdict
from datetime import datetime, timezone
from hashlib import sha256
from typing import Any, Dict, List, Optional


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass(frozen=True)
class EventContract:
    event_id: str
    device_id: str
    business_id: str
    branch_id: str
    sequence: int
    occurred_at: str
    source: str
    network: str
    type: str
    amount: str
    currency: str
    provider_reference: str
    customer_identifier_if_present: Optional[str] = None
    parser_version: str = "v1"
    fingerprint: Optional[str] = None
    confidence: float = 1.0

    @classmethod
    def from_event_dict(cls, data: Dict[str, Any]) -> "EventContract":
        required = [
            "event_id",
            "device_id",
            "business_id",
            "branch_id",
            "sequence",
            "occurred_at",
            "source",
            "network",
            "type",
            "amount",
            "currency",
            "provider_reference",
        ]
        missing = [key for key in required if key not in data]
        if missing:
            raise ValueError(f"Missing required event fields: {missing}")
        payload = dict(data)
        payload["fingerprint"] = payload.get("fingerprint") or compute_fingerprint(payload)
        return cls(**payload)

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)


def compute_fingerprint(data: Dict[str, Any]) -> str:
    canonical = "|".join(
        [
            str(data.get("event_id", "")),
            str(data.get("device_id", "")),
            str(data.get("business_id", "")),
            str(data.get("branch_id", "")),
            str(data.get("sequence", "")),
            str(data.get("occurred_at", "")),
            str(data.get("source", "")),
            str(data.get("network", "")),
            str(data.get("type", "")),
            str(data.get("amount", "")),
            str(data.get("currency", "")),
            str(data.get("provider_reference", "")),
            str(data.get("customer_identifier_if_present", "")),
            str(data.get("parser_version", "v1")),
            str(data.get("confidence", 1.0)),
        ]
    )
    return sha256(canonical.encode("utf-8")).hexdigest()


@dataclass(frozen=True)
class DeviceRegistration:
    business_id: str
    branch_id: str
    device_id: str
    role: str
    credential_public_key: str
    app_version: str
    os_version: str
    status: str = "ACTIVE"


@dataclass(frozen=True)
class SignedSyncRequest:
    device_id: str
    business_id: str
    branch_id: str
    sequence: int
    transaction: EventContract
    device_public_key: str
    signature: str
    request_id: str
    issued_at: str

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)


@dataclass(frozen=True)
class SyncAck:
    request_id: str
    event_id: str
    status: str
    canonical_transaction_id: Optional[str] = None
    message: str = ""


@dataclass(frozen=True)
class SyncOutboxItem:
    outbox_id: str
    business_id: str
    branch_id: str
    device_id: str
    event_id: str
    sequence: int
    payload_version: str
    created_at: str
    attempt_count: int = 0
    status: str = "PENDING"


def build_signed_request(event: EventContract, device: DeviceRegistration) -> SignedSyncRequest:
    issued_at = utc_now().isoformat()
    request_id = f"req-{event.event_id}-{device.device_id}"
    signature = sha256(f"{event.fingerprint}|{device.credential_public_key}".encode("utf-8")).hexdigest()
    return SignedSyncRequest(
        device_id=device.device_id,
        business_id=device.business_id,
        branch_id=device.branch_id,
        sequence=event.sequence,
        transaction=event,
        device_public_key=device.credential_public_key,
        signature=signature,
        request_id=request_id,
        issued_at=issued_at,
    )


def validate_signed_request(request: SignedSyncRequest) -> bool:
    expected = sha256(f"{request.transaction.fingerprint}|{request.device_public_key}".encode("utf-8")).hexdigest()
    return request.signature == expected
