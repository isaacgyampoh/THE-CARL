import unittest

from prototype.api_simulator import ApiSimulator, HubApiClient
from prototype.contracts import DeviceRegistration, EventContract, SignedSyncRequest, build_signed_request


class ApiSimulatorTests(unittest.TestCase):
    def test_api_accepts_valid_signed_request(self):
        api = ApiSimulator()
        device = DeviceRegistration(
            business_id="biz-1",
            branch_id="branch-1",
            device_id="dev-1",
            role="TRANSACTION_DEVICE",
            credential_public_key="pk-dev-1",
            app_version="1.0.0",
            os_version="Android 14",
        )
        event = EventContract(
            event_id="evt-api-1",
            device_id="dev-1",
            business_id="biz-1",
            branch_id="branch-1",
            sequence=1,
            occurred_at="2026-08-15T11:20:00Z",
            source="transaction-device",
            network="MTN",
            type="DEPOSIT",
            amount="18.75",
            currency="GHS",
            provider_reference="ref-api-1",
            fingerprint=None,
        )
        request = build_signed_request(event, device)
        response = api.receive(request)
        self.assertEqual(response.status, "POSTED")
        self.assertEqual(response.event_id, "evt-api-1")

    def test_api_rejects_invalid_signature(self):
        api = ApiSimulator()
        device = DeviceRegistration(
            business_id="biz-1",
            branch_id="branch-1",
            device_id="dev-1",
            role="TRANSACTION_DEVICE",
            credential_public_key="pk-dev-1",
            app_version="1.0.0",
            os_version="Android 14",
        )
        event = EventContract(
            event_id="evt-api-2",
            device_id="dev-1",
            business_id="biz-1",
            branch_id="branch-1",
            sequence=2,
            occurred_at="2026-08-15T11:21:00Z",
            source="transaction-device",
            network="MTN",
            type="DEPOSIT",
            amount="12.00",
            currency="GHS",
            provider_reference="ref-api-2",
            fingerprint=None,
        )
        request = build_signed_request(event, device)
        request = SignedSyncRequest(
            device_id=request.device_id,
            business_id=request.business_id,
            branch_id=request.branch_id,
            sequence=request.sequence,
            transaction=request.transaction,
            device_public_key=request.device_public_key,
            signature="bad-signature",
            request_id=request.request_id,
            issued_at=request.issued_at,
        )
        response = api.receive(request)
        self.assertEqual(response.status, "REJECTED")


if __name__ == "__main__":
    unittest.main()
