import os
import tempfile
import unittest

from prototype.local_store import OfflineStore
from prototype.recovery_simulator import RecoverySimulator


class RecoverySimulatorTests(unittest.TestCase):
    def test_restart_recovery_replays_only_unacked_events(self):
        fd, path = tempfile.mkstemp(suffix=".sqlite")
        os.close(fd)
        store = OfflineStore(path)
        try:
            store.upsert_device("dev-r", "biz-r", "branch-r", "TRANSACTION_DEVICE")
            event1 = {
                "event_id": "evt-recover-1",
                "device_id": "dev-r",
                "business_id": "biz-r",
                "branch_id": "branch-r",
                "sequence": 1,
                "occurred_at": "2026-08-15T10:00:00Z",
                "source": "transaction-device",
                "network": "MTN",
                "event_type": "DEPOSIT",
                "amount": "12.00",
                "currency": "GHS",
                "provider_reference": "ref-1",
                "parser_version": "v1",
                "fingerprint": "fp-1",
                "confidence": 1.0,
            }
            event2 = {
                "event_id": "evt-recover-2",
                "device_id": "dev-r",
                "business_id": "biz-r",
                "branch_id": "branch-r",
                "sequence": 2,
                "occurred_at": "2026-08-15T10:10:00Z",
                "source": "transaction-device",
                "network": "MTN",
                "event_type": "WITHDRAWAL",
                "amount": "3.50",
                "currency": "GHS",
                "provider_reference": "ref-2",
                "parser_version": "v1",
                "fingerprint": "fp-2",
                "confidence": 1.0,
            }
            store.save_event(event1)
            store.save_event(event2)
            store.enqueue_outbox(event1)
            store.enqueue_outbox(event2)
            simulator = RecoverySimulator(store)
            result = simulator.restart_and_recover()
            self.assertEqual(len(result.replayed_event_ids), 2)
            self.assertEqual(result.duplicate_blocked, [])
            self.assertEqual(len(result.pending_after), 0)
        finally:
            store.close()
            os.remove(path)

    def test_duplicate_replay_after_restart_is_blocked(self):
        fd, path = tempfile.mkstemp(suffix=".sqlite")
        os.close(fd)
        store = OfflineStore(path)
        try:
            store.upsert_device("dev-r2", "biz-r2", "branch-r2", "TRANSACTION_DEVICE")
            event = {
                "event_id": "evt-recover-dup",
                "device_id": "dev-r2",
                "business_id": "biz-r2",
                "branch_id": "branch-r2",
                "sequence": 1,
                "occurred_at": "2026-08-15T10:20:00Z",
                "source": "transaction-device",
                "network": "MTN",
                "event_type": "DEPOSIT",
                "amount": "40.00",
                "currency": "GHS",
                "provider_reference": "ref-dup",
                "parser_version": "v1",
                "fingerprint": "fp-dup",
                "confidence": 1.0,
            }
            store.save_event(event)
            store.enqueue_outbox(event)
            simulator = RecoverySimulator(store)
            first = simulator.restart_and_recover()
            self.assertEqual(len(first.replayed_event_ids), 1)

            # Simulate a second pending replay on restart before the original outbox row is acknowledged.
            store.enqueue_outbox(event)
            second = simulator.restart_and_recover()
            self.assertEqual(second.duplicate_blocked, ["evt-recover-dup"])
        finally:
            store.close()
            os.remove(path)


if __name__ == "__main__":
    unittest.main()
