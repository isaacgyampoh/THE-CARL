import os
import tempfile
import unittest

from prototype.local_store import OfflineStore


class OfflineStoreTests(unittest.TestCase):
    def test_device_and_event_persist(self):
        fd, path = tempfile.mkstemp(suffix=".sqlite")
        os.close(fd)
        store = OfflineStore(path)
        try:
            store.upsert_device("dev-1", "biz-1", "branch-1", "TRANSACTION_DEVICE")
            event = {
                "event_id": "evt-db-1",
                "device_id": "dev-1",
                "business_id": "biz-1",
                "branch_id": "branch-1",
                "sequence": 1,
                "occurred_at": "2026-08-15T10:00:00Z",
                "source": "transaction-device",
                "network": "MTN",
                "event_type": "DEPOSIT",
                "amount": "10.00",
                "currency": "GHS",
                "provider_reference": "ref-1",
                "parser_version": "v1",
                "fingerprint": "abc123",
                "confidence": 1.0,
            }
            store.save_event(event)
            rows = store.connection.execute("SELECT COUNT(*) FROM transactions WHERE event_id = ?", ("evt-db-1",)).fetchone()
            self.assertEqual(rows[0], 1)
        finally:
            store.close()
            os.remove(path)

    def test_outbox_recovery_keeps_pending_items(self):
        fd, path = tempfile.mkstemp(suffix=".sqlite")
        os.close(fd)
        store = OfflineStore(path)
        try:
            store.upsert_device("dev-2", "biz-2", "branch-2", "TRANSACTION_DEVICE")
            event = {
                "event_id": "evt-db-2",
                "device_id": "dev-2",
                "business_id": "biz-2",
                "branch_id": "branch-2",
                "sequence": 2,
                "occurred_at": "2026-08-15T10:05:00Z",
                "source": "transaction-device",
                "network": "MTN",
                "event_type": "WITHDRAWAL",
                "amount": "15.50",
                "currency": "GHS",
                "provider_reference": "ref-2",
                "parser_version": "v1",
                "fingerprint": "xyz987",
                "confidence": 1.0,
            }
            store.save_event(event)
            store.enqueue_outbox(event)
            pending = store.recover_pending()
            self.assertEqual(len(pending), 1)
            self.assertEqual(pending[0]["event_id"], "evt-db-2")
            store.mark_outbox_acked("evt-db-2")
            self.assertEqual(store.list_pending_outbox(), [])
        finally:
            store.close()
            os.remove(path)

    def test_sync_attempt_logging_is_recorded(self):
        fd, path = tempfile.mkstemp(suffix=".sqlite")
        os.close(fd)
        store = OfflineStore(path)
        try:
            store.log_sync_attempt("evt-db-3", "POSTED", "accepted")
            rows = store.connection.execute("SELECT COUNT(*) FROM sync_attempts WHERE event_id = ?", ("evt-db-3",)).fetchone()
            self.assertEqual(rows[0], 1)
        finally:
            store.close()
            os.remove(path)


if __name__ == "__main__":
    unittest.main()
