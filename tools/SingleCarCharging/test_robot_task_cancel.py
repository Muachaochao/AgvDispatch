"""Cancellation reconciliation without network or motion."""
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import robot_task_cancel as runner
from test_charger_session_runner import Clock, Evidence


class CancelTests(unittest.TestCase):
    def execute(self, statuses, stop_flags=None, unknown=False):
        clock = Clock()
        calls = []
        states = iter(stop_flags or [True])
        status_values = iter(statuses)
        last_status = statuses[-1]
        last_stop = False

        class Client:
            def request(self, api, port, payload, **kwargs):
                nonlocal last_status, last_stop
                calls.append((api, payload))
                if api == 1000:
                    return {"id": "test-hardware"}
                if api == 1110:
                    last_status = next(status_values, last_status)
                    return {"task_status_package": {"task_status_list": [
                        {"task_id": "original", "status": last_status}]}}
                if api == 1100:
                    last_stop = next(states, last_stop)
                    return {"is_stop": last_stop, "chassisIsStop": last_stop,
                            "vx": 0.0, "vy": 0.0, "w": 0.0}
                if api == 3003:
                    if unknown:
                        raise runner.ResultUnknown("cancel reply lost")
                    return {"ret_code": 0}
                raise AssertionError(api)

        options = SimpleNamespace(robot="192.0.2.1", expected_id="test-hardware",
                                  task_id="original", timeout_seconds=2,
                                  evidence_dir="unused")
        with patch.object(runner, "Robokit", return_value=Client()), \
                patch.object(runner, "Evidence", return_value=Evidence()), \
                patch.object(runner, "time", clock):
            result, code = runner.run(options)
        return result, code, calls

    def test_cancel_requires_observed_stopped_state(self):
        result, code, calls = self.execute([2, 6, 6], [False, True])
        self.assertEqual(("cancelled", 0), (result["outcome"], code))
        self.assertEqual(1, sum(api == 3003 for api, _ in calls))

    def test_terminal_task_without_stopping_is_unknown(self):
        result, code, _ = self.execute([2, 6], [False])
        self.assertEqual(("unknown", 3), (result["outcome"], code))

    def test_timeout_is_failure_not_cancellation(self):
        result, code, _ = self.execute([2, 7])
        self.assertEqual(("task_failed", 2), (result["outcome"], code))

    def test_completed_before_cancel_is_reported_separately(self):
        result, code, _ = self.execute([2, 4])
        self.assertEqual(("completed_before_cancel", 0), (result["outcome"], code))

    def test_terminal_history_never_triggers_another_cancel(self):
        for status in (4, 5, 6, 7):
            with self.subTest(status=status):
                result, code, calls = self.execute([status])
                self.assertEqual("already_terminal", result["outcome"])
                self.assertFalse(any(api == 3003 for api, _ in calls))

    def test_unknown_cancel_response_is_never_retried(self):
        result, code, calls = self.execute([2], unknown=True)
        self.assertEqual(("unknown", 3), (result["outcome"], code))
        self.assertEqual(1, sum(api == 3003 for api, _ in calls))


if __name__ == "__main__":
    unittest.main()
