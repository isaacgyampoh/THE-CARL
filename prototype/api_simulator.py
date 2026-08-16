from __future__ import annotations

from dataclasses import dataclass
from typing import Dict, List, Optional

from prototype.contracts import EventContract, SignedSyncRequest, SyncAck, validate_signed_request


@dataclass
class ApiLedger:
    posted: Dict[str, str] = None

    def __post_init__(self) -> None:
        if self.posted is None:
            self.posted = {}

    def post(self, request: SignedSyncRequest) -> SyncAck:
        if request.event_id if hasattr(request, "event_id") else False:
            pass
        if request.request_id in self.posted:
            return SyncAck(
                request_id=request.request_id,
                event_id=request.transaction.event_id,
                status="DUPLICATE",
                message="request-already-processed",
            )
        if not validate_signed_request(request):
            return SyncAck(
                request_id=request.request_id,
                event_id=request.transaction.event_id,
                status="REJECTED",
                message="invalid-signature",
            )
        self.posted[request.request_id] = request.transaction.event_id
        return SyncAck(
            request_id=request.request_id,
            event_id=request.transaction.event_id,
            status="POSTED",
            canonical_transaction_id=f"canon-{request.transaction.event_id}",
            message="accepted",
        )


class ApiSimulator:
    def __init__(self) -> None:
        self.ledger = ApiLedger()
        self.audit_log: List[Dict[str, str]] = []

    def receive(self, request: SignedSyncRequest) -> SyncAck:
        response = self.ledger.post(request)
        self.audit_log.append(
            {
                "request_id": request.request_id,
                "event_id": request.transaction.event_id,
                "status": response.status,
            }
        )
        return response


class HubApiClient:
    def __init__(self, api: ApiSimulator) -> None:
        self.api = api

    def send(self, request: SignedSyncRequest) -> SyncAck:
        return self.api.receive(request)
