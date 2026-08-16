import unittest

from prototype.hub_service import HubService, SignedEventEnvelope
from prototype.models import CloudLedger, Device, DeviceRegistry, Event, HubQueue, utc_now


class HubServiceTests(unittest.TestCase):
    def setUp(self):
        self.registry = DeviceRegistry()
        self.device = Device(device_id="dev-1", business_id="biz-1", branch_id="branch-1", role="TRANSACTION_DEVICE")
        self.registry.register(self.device)
        self.queue = HubQueue()
        self.ledger = CloudLedger()
        self.service = HubService(self.registry, self.queue, self.ledger)

    def test_hub_accepts_valid_signed_event(self):
        event = Event(
            event_id="evt-hub-1",
            device_id="dev-1",
            business_id="biz-1",
            branch_id="branch-1",
            sequence=1,
            occurred_at=utc_now(),
            source="transaction-device",
            network="MTN",
            event_type="DEPOSIT",
            amount="20.00",
            currency="GHS",
            provider_reference="ref-hub-1",
        )
        envelope = SignedEventEnvelope(event=event, signature="sig-1", device_public_key="device-key-dev-1")
        response = self.service.ingest(envelope)
        self.assertEqual(response.status, "POSTED")
        self.assertIn(event.event_id, self.ledger.posted_event_ids)

    def test_hub_rejects_unknown_device(self):
        event = Event(
            event_id="evt-hub-2",
            device_id="missing-device",
            business_id="biz-1",
            branch_id="branch-1",
            sequence=2,
            occurred_at=utc_now(),
            source="transaction-device",
            network="MTN",
            event_type="DEPOSIT",
            amount="15.00",
            currency="GHS",
            provider_reference="ref-hub-2",
        )
        response = self.service.ingest(SignedEventEnvelope(event=event, signature="sig-2", device_public_key="device-key-missing-device"))
        self.assertEqual(response.status, "REJECTED")

    def test_hub_rejects_identity_mismatch(self):
        event = Event(
            event_id="evt-hub-3",
            device_id="dev-1",
            business_id="biz-1",
            branch_id="branch-1",
            sequence=3,
            occurred_at=utc_now(),
            source="transaction-device",
            network="MTN",
            event_type="DEPOSIT",
            amount="30.00",
            currency="GHS",
            provider_reference="ref-hub-3",
        )
        response = self.service.ingest(SignedEventEnvelope(event=event, signature="sig-3", device_public_key="wrong-key"))
        self.assertEqual(response.status, "REJECTED")


if __name__ == "__main__":
    unittest.main()
