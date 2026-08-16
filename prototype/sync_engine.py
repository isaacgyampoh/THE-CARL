from __future__ import annotations

from dataclasses import dataclass, field
from typing import Dict, List, Optional

from prototype.models import Event


@dataclass
class OutboxQueueItem:
    event_id: str
    device_id: str
    business_id: str
    branch_id: str
    sequence: int
    payload: Event
    status: str = "PENDING"
    attempts: int = 0
    last_error: Optional[str] = None
    acked: bool = False


class SyncEngine:
    def __init__(self, ledger_sink=None) -> None:
        self.ledger_sink = ledger_sink
        self.queue: Dict[str, OutboxQueueItem] = {}
        self.acknowledged: set[str] = set()

    def enqueue(self, event: Event) -> OutboxQueueItem:
        item = OutboxQueueItem(
            event_id=event.event_id,
            device_id=event.device_id,
            business_id=event.business_id,
            branch_id=event.branch_id,
            sequence=event.sequence,
            payload=event,
        )
        self.queue[event.event_id] = item
        return item

    def pending_items(self) -> List[OutboxQueueItem]:
        return [item for item in self.queue.values() if not item.acked]

    def process_next(self, event_id: str, *, success: bool = True, error: Optional[str] = None) -> OutboxQueueItem:
        item = self.queue[event_id]
        item.attempts += 1
        if success:
            item.status = "ACKED"
            item.acked = True
            self.acknowledged.add(event_id)
            if self.ledger_sink is not None:
                self.ledger_sink(event)
            return item

        item.status = "RETRY"
        item.last_error = error or "sync-failed"
        return item

    def process_all(self, *, simulate_failures: Optional[set[str]] = None) -> List[OutboxQueueItem]:
        simulate_failures = simulate_failures or set()
        processed: List[OutboxQueueItem] = []
        for event_id in list(self.queue.keys()):
            item = self.queue[event_id]
            if item.acked:
                continue
            if event_id in simulate_failures:
                processed.append(self.process_next(event_id, success=False, error="network-timeout"))
            else:
                processed.append(self.process_next(event_id, success=True))
        return processed

    def retry_pending(self, max_attempts: int = 3) -> List[OutboxQueueItem]:
        results: List[OutboxQueueItem] = []
        for item in self.pending_items():
            if item.attempts >= max_attempts:
                item.status = "FAILED"
                item.last_error = item.last_error or "max-attempts-exceeded"
                results.append(item)
                continue
            if item.status == "RETRY":
                item.status = "PENDING"
            results.append(self.process_next(item.event_id, success=False, error="retrying"))
        return results

    def is_acked(self, event_id: str) -> bool:
        return event_id in self.acknowledged
