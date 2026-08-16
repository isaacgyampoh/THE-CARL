import unittest

from prototype.models import Event, Device
from prototype.sync_engine import SyncEngine


class SyncEngineTests(unittest.TestCase):
    def test_enqueue_and_process_success(self):
        engine = SyncEngine()
        event = Event(
            event_id="evt-sync-1",
            device_id="dev-1",
            business_id="biz-1",
            branch_id="branch-1",
            sequence=1,
            occurred_at=event_occurred(),
            source="transaction-device",
            network="MTN",
            event_type="DEPOSIT",
            amount="10.00",
            currency="GHS",
            provider_reference="ref-1",
        )
        item = engine.enqueue(event)
        processed = engine.process_next(item.event_id, success=True)
        self.assertEqual(processed.status, "ACKED")
        self.assertTrue(engine.is_acked(event.event_id))

    def test_process_all_handles_retry_cases(self):
        engine = SyncEngine()
        event = Event(
            event_id="evt-sync-2",
            device_id="dev-2",
            business_id="biz-1",
            branch_id="branch-1",
            sequence=2,
            occurred_at=event_occurred(),
            source="transaction-device",
            network="MTN",
            event_type="WITHDRAWAL",
            amount="7.50",
            currency="GHS",
            provider_reference="ref-2",
        )
        engine.enqueue(event)
        fails = engine.process_all(simulate_failures={event.event_id})
        self.assertEqual(fails[0].status, "RETRY")
        self.assertEqual(fails[0].last_error, "network-timeout")

    def test_retry_pending_max_attempts_is_tracked(self):
        engine = SyncEngine()
        event = Event(
            event_id="evt-sync-3",
            device_id="dev-3",
            business_id="biz-1",
            branch_id="branch-1",
            sequence=3,
            occurred_at=event_occurred(),
            source="transaction-device",
            network="MTN",
            event_type="TRANSFER",
            amount="50.00",
            currency="GHS",
            provider_reference="ref-3",
        )
        item = engine.enqueue(event)
        for _ in range(3):
            engine.process_next(item.event_id, success=False, error="retrying")
        retry_results = engine.retry_pending(max_attempts=3)
        self.assertEqual(retry_results[0].status, "FAILED")


def event_occurred():
    from datetime import datetime, timezone
    return datetime.now(timezone.utc)


if __name__ == "__main__":
    unittest.main()
