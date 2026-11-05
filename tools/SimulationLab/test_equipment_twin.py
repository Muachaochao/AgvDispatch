"""Optional elevator/door and signal-generator model tests."""
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch

from equipment_twin import EquipmentModel


class EquipmentModelTests(unittest.TestCase):
    def test_rejected_plan_does_not_partially_move_or_clear_existing_signals(self):
        with tempfile.TemporaryDirectory() as name:
            model = EquipmentModel(name)
            try:
                model.control(dict(sequence='original', signals=dict(set=dict(call=False))))
                with self.assertRaises(ValueError):
                    model.control(dict(sequence='bad', travelSeconds=.3,
                        elevator=dict(targetFloor='2'), signals=dict(set=dict(call=True),
                        schedule=[dict(id='bad', afterMs=-1, set=dict(call=False))])))
                self.assertEqual(model.travel_seconds, 2)
                self.assertIsNone(model.status()['targetFloor'])
                self.assertFalse(model.status()['signals']['call'])
                self.assertEqual(model.status()['sequence'], 'original')
            finally: model.stop()

    def test_signal_order_follows_due_time_even_when_multiple_frames_are_late(self):
        with tempfile.TemporaryDirectory() as name:
            model = EquipmentModel(name)
            try:
                with model.lock:
                    model.control(dict(sequence='plan', signals=dict(schedule=[
                        dict(id='later', afterMs=20, set=dict(call=False)),
                        dict(id='earlier', afterMs=10, set=dict(call=True))])))
                    time.sleep(.15)  # Both deadlines pass while model is blocked.
                end = time.monotonic() + 1
                while model.status()['schedules'] and time.monotonic() < end: time.sleep(.02)
                self.assertFalse(model.status()['signals']['call'])
                self.assertEqual([e['source'] for e in model.status()['signalEvents']], ['earlier', 'later'])
            finally: model.stop()

    def test_snapshot_file_contention_does_not_kill_device_thread(self):
        with tempfile.TemporaryDirectory() as name:
            model = EquipmentModel(name)
            try:
                with patch.object(Path, 'replace', side_effect=PermissionError('reader busy')):
                    end = time.monotonic() + 1
                    while not model.status()['error'] and time.monotonic() < end: time.sleep(.02)
                    self.assertIn('正在重试', model.status()['error'])
                    self.assertTrue(model.thread.is_alive())
                end = time.monotonic() + 1
                while model.status()['error'] and time.monotonic() < end: time.sleep(.02)
                self.assertIsNone(model.status()['error'])
                self.assertTrue(model.thread.is_alive())
            finally: model.stop()

    def test_invalid_number_conflicting_motion_and_accumulated_plan_limit(self):
        with tempfile.TemporaryDirectory() as name:
            model = EquipmentModel(name)
            try:
                for value in (float('nan'), float('inf')):
                    with self.assertRaises(ValueError):
                        model.control(dict(sequence='bad', signals=dict(set=dict(value=value))))
                with self.assertRaises(ValueError):
                    model.control(dict(sequence='conflict', elevator=dict(targetFloor='2', doorCommand='open')))
                model.control(dict(sequence='many', signals=dict(schedule=[
                    dict(id=str(i), afterMs=100000, set=dict(call=True)) for i in range(200)])))
                with self.assertRaises(ValueError):
                    model.control(dict(sequence='extra', signals=dict(schedule=[
                        dict(id='extra', afterMs=100000, set=dict(call=False))])))
                self.assertEqual(len(model.status()['schedules']), 200)
                external = model.status()
                external['schedules'].clear()
                self.assertEqual(len(model.status()['schedules']), 200)
            finally: model.stop()

    def test_elevator_moves_between_floors_and_doors_open(self):
        with tempfile.TemporaryDirectory() as name:
            model = EquipmentModel(name)
            try:
                model.control(dict(sequence='move', travelSeconds=.2, doorSeconds=.2,
                    elevator=dict(targetFloor='2'), signals={}))
                end = time.monotonic() + 2
                while time.monotonic() < end and model.status()['currentFloor'] != '2': time.sleep(.05)
                self.assertEqual(model.status()['currentFloor'], '2')
                model.control(dict(sequence='open', elevator=dict(doorCommand='open'), signals={}))
                end = time.monotonic() + 1
                while time.monotonic() < end and model.status()['door'] != 'Open': time.sleep(.05)
                self.assertEqual(model.status()['door'], 'Open')
            finally: model.stop()

    def test_signal_generator_time_axis_and_fault_reset(self):
        with tempfile.TemporaryDirectory() as name:
            model = EquipmentModel(name)
            try:
                model.control(dict(sequence='signals', elevator={}, signals=dict(
                    set=dict(call=False),
                    schedule=[dict(id='call', afterMs=80, set=dict(call=True, count=2))])))
                self.assertFalse(model.status()['signals']['call'])
                end = time.monotonic() + 1
                while time.monotonic() < end and not model.status()['signals'].get('call'): time.sleep(.03)
                self.assertTrue(model.status()['signals']['call'])
                self.assertEqual(model.status()['signals']['count'], 2)
                model.control(dict(sequence='fault', elevator=dict(fault=True), signals={}))
                self.assertTrue(model.status()['fault'])
                model.control(dict(sequence='reset', elevator=dict(reset=True), signals={}))
                self.assertFalse(model.status()['fault'])
            finally: model.stop()

    def test_invalid_signal_and_movement_while_open_are_rejected(self):
        with tempfile.TemporaryDirectory() as name:
            model = EquipmentModel(name)
            try:
                with self.assertRaises(ValueError):
                    model.control(dict(sequence='bad', elevator={}, signals=dict(set={'bad': object()})))
                model.control(dict(sequence='open', elevator=dict(doorCommand='open'), signals={}))
                with self.assertRaises(ValueError):
                    model.control(dict(sequence='move', elevator=dict(targetFloor='2'), signals={}))
            finally: model.stop()


if __name__ == '__main__': unittest.main()
