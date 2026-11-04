"""Optional elevator/door and signal-generator model tests."""
import tempfile
import time
import unittest

from equipment_twin import EquipmentModel


class EquipmentModelTests(unittest.TestCase):
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
