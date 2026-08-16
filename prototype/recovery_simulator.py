from __future__ import annotations

from dataclasses import dataclass
from typing import Dict, List, Optional

from prototype.local_store import OfflineStore


@dataclass
class RecoveryResult:
    pending_before: List[Dict[str, object]]
    pending_after: List[Dict[str, object]]
    replayed_event_ids: List[str]
    duplicate_blocked: List[str]


class RecoverySimulator:
    def __init__(self, store: OfflineStore) -> None:
        self.store = store
        self.processed_event_ids: set[str] = set()

    def restart_and_recover(self, *, replayed_event_ids: Optional[List[str]] = None) -> RecoveryResult:
        replayed_event_ids = replayed_event_ids or []
        pending_before = self.store.recover_pending()
        pending_after: List[Dict[str, object]] = []
        duplicate_blocked: List[str] = []

        for row in pending_before:
            event_id = row["event_id"]
            if event_id in self.processed_event_ids:
                duplicate_blocked.append(event_id)
                continue
            self.processed_event_ids.add(event_id)
            replayed_event_ids.append(event_id)
            pending_after.append(row)

        for row in pending_after:
            self.store.mark_outbox_acked(row["event_id"], status="ACKED")

        return RecoveryResult(
            pending_before=pending_before,
            pending_after=self.store.recover_pending(),
            replayed_event_ids=replayed_event_ids,
            duplicate_blocked=duplicate_blocked,
        )

    def reset(self) -> None:
        self.processed_event_ids.clear()
