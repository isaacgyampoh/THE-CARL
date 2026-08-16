import unittest

from prototype.contracts import DeviceRegistration, EventContract, SignedSyncRequest, build_signed_request, compute_fingerprint, validate_signed_request


class ContractTests(unittest.TestCase):
    def test_event_contract_computes_fingerprint(self):
        payload = {
            "event_id": "evt-1",
            "device_id": "dev-1",
            "business_id": "biz-1",
            "branch_id": "branch-1",
            "sequence": 1,
            "occurred_at": "2026-08-15T11:00:00Z",
            "source": "transaction-device",
            "network": "MTN",
            "type": "DEPOSIT",
            "amount": "20.00",
            "currency": "GHS",
            "provider_reference": "ref-1",
            "parser_version": "v1",
            "confidence": 1.0,
        }
        fp = compute_fingerprint(payload)
        self.assertTrue(len(fp) > 20)
        contract = EventContract.from_event_dict(payload)
        self.assertEqual(contract.fingerprint, fp)

    def test_signed_request_validation_passes_for_matching_signature(self):
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
            event_id="evt-2",
            device_id="dev-1",
            business_id="biz-1",
            branch_id="branch-1",
            sequence=2,
            occurred_at="2026-08-15T11:05:00Z",
            source="transaction-device",
            network="MTN",
            type="DEPOSIT",
            amount="5.25",
            currency="GHS",
            provider_reference="ref-2",
            fingerprint=None,
        )
        signed = build_signed_request(event, device)
        self.assertTrue(validate_signed_request(signed))
        self.assertEqual(signed.device_public_key, "pk-dev-1")

    def test_missing_required_fields_fail_validation(self):
        with self.assertRaises(ValueError):
            EventContract.from_event_dict({
                "event_id": "evt-3",
                "device_id": "dev-1",
            })


if __name__ == "__main__":
    unittest.main()
