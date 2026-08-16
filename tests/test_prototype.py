import unittest
from datetime import timedelta

from prototype.models import CloudLedger, Device, DeviceRegistry, Event, HubQueue, SyncCoordinator, TransactionDeviceClient, utc_now


class PrototypeTests(unittest.TestCase):
    def setUp(self):
        self.registry = DeviceRegistry()
        self.device = Device(device_id="dev-1", business_id="biz-1", branch_id="branch-1", role="TRANSACTION_DEVICE")
        self.registry.register(self.device)
        self.hub_queue = HubQueue()
        self.ledger = CloudLedger()
        self.coordinator = SyncCoordinator(self.registry, self.hub_queue, self.ledger)
        self.client = TransactionDeviceClient(self.device)

    def test_valid_event_posts_once(self):
        event = self.client.create_event(1, "10.00", "DEPOSIT", "ref-1")
        result = self.coordinator.accept_event(event)
        self.assertEqual(result, "POSTED")
        self.assertEqual(self.ledger.posted_event_ids, {event.event_id})

    def test_duplicate_event_is_blocked(self):
        event = self.client.create_event(1, "10.00", "DEPOSIT", "ref-1")
        first = self.coordinator.accept_event(event)
        second = self.coordinator.accept_event(event)
        self.assertEqual(first, "POSTED")
        self.assertEqual(second, "DUPLICATE")

    def test_revoked_device_is_rejected(self):
        revoked = Device(device_id="revoked-1", business_id="biz-1", branch_id="branch-1", role="TRANSACTION_DEVICE", revoked=True)
        self.registry.register(revoked)
        event = Event(
            event_id="evt-revoked-1",
            device_id="revoked-1",
            business_id="biz-1",
            branch_id="branch-1",
            sequence=1,
            occurred_at=utc_now(),
            source="transaction-device",
            network="MTN",
            event_type="DEPOSIT",
            amount="5.00",
            currency="GHS",
            provider_reference="ref-revoked",
        )
        self.assertEqual(self.coordinator.accept_event(event), "REJECTED")

    def test_cross_business_event_is_rejected(self):
        event = Event(
            event_id="evt-cross-1",
            device_id=self.device.device_id,
            business_id="biz-2",
            branch_id="branch-1",
            sequence=2,
            occurred_at=utc_now(),
            source="transaction-device",
            network="MTN",
            event_type="DEPOSIT",
            amount="5.00",
            currency="GHS",
            provider_reference="ref-cross",
        )
        self.assertEqual(self.coordinator.accept_event(event), "REJECTED")

    def test_outbox_flush_works_for_safely_queued_events(self):
        self.client.create_event(1, "12.50", "WITHDRAWAL", "ref-2")
        results = self.client.flush_outbox(self.coordinator)
        self.assertEqual(results, ["POSTED"])
        self.assertEqual(len(self.client.outbox), 1)
        self.assertEqual(self.client.outbox[0].status, "SENT")


if __name__ == "__main__":
    unittest.main()
