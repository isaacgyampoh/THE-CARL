import unittest

from prototype.end_to_end_flow import EndToEndFlow


class EndToEndFlowTests(unittest.TestCase):
    def test_end_to_end_flow_posts_and_recovers(self):
        flow = EndToEndFlow()
        result = flow.run()
        self.assertEqual(result.api_status, "POSTED")
        self.assertEqual(result.queue_after_sync, 1)
        self.assertFalse(result.duplicate_detected)
        self.assertIn(result.device_event.event_id, result.recovered_after_restart)


if __name__ == "__main__":
    unittest.main()
