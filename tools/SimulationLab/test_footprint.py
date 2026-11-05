import math
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'WebSingleVehicleLab'))
from footprint import rectangles_overlap, swept_rectangles_overlap, vehicle_extents


class VehicleFootprintTests(unittest.TestCase):
    def test_confirmed_vehicle_dimensions_do_not_use_invented_one_point_two_diameter(self):
        self.assertEqual(vehicle_extents(dict(LengthMeters=.8, WidthMeters=.52)), (.8, .52))
        self.assertEqual(vehicle_extents(dict(RadiusFromReportedReferenceMeters=.49)), (.98, .98))
        with self.assertRaises(ValueError):
            vehicle_extents(dict(LengthMeters=.8))

    def test_lm15_and_lm26_poses_do_not_overlap_for_800_by_520_vehicles(self):
        agv01 = dict(x=5.415, y=-4.559, angle=0, lengthMeters=.8, widthMeters=.52)
        agv03 = dict(x=5.763, y=-3.358, angle=-.890013679382844, lengthMeters=.8, widthMeters=.52)
        self.assertFalse(rectangles_overlap(agv01, agv03, .05))

    def test_facing_and_crossing_rectangles_still_detect_real_overlap(self):
        self.assertTrue(rectangles_overlap(
            dict(x=0, y=0, angle=0, lengthMeters=.8, widthMeters=.52),
            dict(x=.82, y=0, angle=0, lengthMeters=.8, widthMeters=.52), .05))
        self.assertTrue(swept_rectangles_overlap(
            dict(x=-1, y=0, angle=0, lengthMeters=.8, widthMeters=.52),
            dict(x=1, y=0, angle=0, lengthMeters=.8, widthMeters=.52),
            dict(x=0, y=-1, angle=math.pi/2, lengthMeters=.8, widthMeters=.52),
            dict(x=0, y=1, angle=math.pi/2, lengthMeters=.8, widthMeters=.52), .05))
        self.assertFalse(rectangles_overlap(
            dict(x=0, y=0, angle=0, lengthMeters=.8, widthMeters=.52),
            dict(x=0, y=1.2, angle=0, lengthMeters=.8, widthMeters=.52), .05))


if __name__ == '__main__':
    unittest.main()
