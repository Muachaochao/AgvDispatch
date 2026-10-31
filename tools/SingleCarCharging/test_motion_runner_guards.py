"""Offline regressions for stale task feedback and motion prerequisites."""
import copy
import hashlib
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import advance_runner
import approach_runner
import probe_runner


class Clock:
    def __init__(self):
        self.now = 0.0

    def monotonic(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds


class Evidence:
    def write_json(self, *args):
        pass

    def write(self, *args):
        pass


def idle():
    return dict(task_status=4, task_id="prior", script_status=0, unfinished_path=[],
                is_stop=True, chassisIsStop=True, vx=0.0, vy=0.0, w=0.0,
                charging=False, manual_charge=False, auto_charge=False,
                blocked=False, emergency=False, soft_emc=False, errors=[], fatals=[],
                current_lock={"locked": False}, dispatch_mode=0,
                x=1.0, y=2.0, angle=1.5, confidence=0.9, current_station="LM52")


class Client:
    evidence = Evidence()

    def __init__(self, state):
        self.state = state

    def request(self, api, port, payload, **kwargs):
        if api == 1000:
            return {"id": "hardware"}
        if api == 1100:
            return self.state
        if api == 1110:
            return {"task_status_list": [{"task_id": "prior", "status": self.state["task_status"]}]}
        raise AssertionError("mutations are forbidden in preflight tests")


class MotionGuardTests(unittest.TestCase):
    def test_completed_and_cancelled_retained_tasks_need_fresh_confirmation(self):
        for status in (4, 6):
            state = dict(idle(), task_status=status)
            result = approach_runner.preflight(Client(state), "hardware", "LM52")
            self.assertEqual(status, result["confirmed_prior_approach"]["status"])

    def test_blocked_emergency_invalid_pose_and_contradictory_motion_refuse(self):
        for key, value in (("blocked", True), ("soft_emc", True), ("vx", 0.1),
                           ("confidence", 0.49), ("x", float("nan")),
                           ("script_status", False), ("dispatch_mode", False),
                           ("errors", None), ("fatals", None)):
            with self.subTest(key=key, value=value):
                with self.assertRaises(probe_runner.Refused):
                    approach_runner.preflight(Client(dict(idle(), **{key: value})), "hardware", "LM52")
        for key in ("blocked", "confidence", "errors", "fatals", "chassisIsStop"):
            state = idle()
            del state[key]
            with self.subTest(missing=key), self.assertRaises(probe_runner.Refused):
                approach_runner.preflight(Client(state), "hardware", "LM52")

    def test_terminal_confirmation_must_not_contain_another_pending_task(self):
        client = Client(idle())
        original = client.request

        def mixed(api, port, payload, **kwargs):
            if api == 1110:
                return {"task_status_list": [{"task_id": "prior", "status": 4},
                                              {"task_id": "other", "status": 1}]}
            return original(api, port, payload, **kwargs)

        client.request = mixed
        with self.assertRaises(ValueError):
            approach_runner.preflight(client, "hardware", "LM52")

    def track(self, module, responses, moving=False):
        state = idle()
        state["task_id"] = "our-task"
        state["vx"] = 0.1 if moving else 0.0
        report = dict(run_id="our-run", phase="completed", status=3, error=None,
                      charging_commanded=False, motion_mode="path")
        if module is advance_runner:
            report["advance"] = module.ADVANCE_MARKER
        else:
            report["approach"] = module.APPROACH_MARKER
        calls = []

        class Tracker:
            evidence = Evidence()

            def request(self, api, port, payload, **kwargs):
                calls.append(api)
                if api == 1110:
                    return responses.pop(0) if len(responses) > 1 else responses[0]
                if api == 1100:
                    # The first runtime has no completed report. A later report
                    # must not be paired with a cached task-status value.
                    return dict(state, move_status_info=report if calls.count(1100) > 1 or moving else None)
                raise AssertionError("tracking must never send mutations")

        clock = Clock()
        track = module.track_advance if module is advance_runner else module.track_approach
        with patch.object(module.time, "monotonic", clock.monotonic), \
                patch.object(module.time, "sleep", clock.sleep):
            return track(Tracker(), "our-task", "our-run", 1.1)

    def test_missing_or_ambiguous_task_feedback_cannot_reuse_previous_completed_status(self):
        valid = {"task_status_list": [{"task_id": "our-task", "status": 4}]}
        for module in (advance_runner, approach_runner):
            for rows in ([], [{"task_id": "another", "status": 4}],
                         [{"task_id": "our-task", "status": 4}] * 2):
                result = self.track(module, [valid, {"task_status_list": rows}])
                self.assertEqual("unknown", result["outcome"])
                self.assertTrue(result["tracking_errors"])

    def test_path_reached_does_not_override_a_moving_chassis(self):
        valid = {"task_status_list": [{"task_id": "our-task", "status": 4}]}
        for module in (advance_runner, approach_runner):
            self.assertEqual("unknown", self.track(module, [valid], moving=True)["outcome"])

    def test_advance_uses_final_preflight_pose_after_script_upload(self):
        before = {"runtime": idle()}
        fresh = copy.deepcopy(before)
        fresh["runtime"]["x"] = 1.02
        args = SimpleNamespace(robot="192.0.2.1", expected_id="hardware", expected_station="LM52",
                               distance_m=0.01, lateral_m=0.0, yaw_delta_rad=0.0,
                               obstacle_stop_dist_m=0.05, motion_mode="path", reach_dist_m=0.001,
                               max_speed=0.005, stage_only=False, script_timeout=30, track_timeout=45)
        source = Path(advance_runner.__file__).with_name("charge_dock_advance.py").read_bytes()
        filename = "charge_dock_advance_" + hashlib.sha256(source).hexdigest()[:12] + ".py"
        sent = []

        class Stager:
            evidence = Evidence()

            def request(self, api, port, payload, **kwargs):
                if api == 1100:
                    return before["runtime"]
                if api == 1506:
                    return {"list": [{"relative_path": filename}]}
                if api == 4022:
                    return source
                if api == 3066:
                    sent.append(payload)
                    return {"ret_code": 0}
                raise AssertionError(api)

        with patch.object(advance_runner, "preflight", side_effect=[before, fresh]), \
                patch.object(advance_runner, "track_advance", return_value={"outcome": "unknown"}):
            result = advance_runner.run_advance(Stager(), args, "offline-run")
        self.assertEqual(1.02, sent[0]["move_task_list"][0]["script_args"]["expected_x"])
        self.assertEqual(1.02, result["start"]["x"])


if __name__ == "__main__":
    unittest.main()
