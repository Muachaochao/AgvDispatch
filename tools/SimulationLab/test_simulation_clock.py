import math
import os
import time
import unittest
from unittest.mock import patch

import simulation_clock


class SimulationClockTests(unittest.TestCase):
    def test_business_rate_scales_elapsed_time_and_delays(self):
        with patch('simulation_clock.time.time', return_value=1_800_000_001):
            clock = simulation_clock.SimulationClock(2, 1_800_000_000)
            self.assertAlmostEqual(clock.monotonic(), 2)
            self.assertAlmostEqual(clock.time(), 1_800_000_002)
        self.assertAlmostEqual(clock.business_delay(10), 5)

    def test_environment_validates_rate_and_origin(self):
        with patch.dict(os.environ, {
            'AGV_SIMULATION_RATE': '4',
            'AGV_SIMULATION_ORIGIN_UNIX_MS': '1800000000000',
        }, clear=False):
            clock = simulation_clock.SimulationClock.from_environment()
        self.assertEqual(clock.rate, 4)
        self.assertEqual(clock.origin_seconds, 1_800_000_000)
        with patch.dict(os.environ, {'AGV_SIMULATION_RATE': '9'}, clear=False):
            with self.assertRaises(ValueError):
                simulation_clock.SimulationClock.from_environment()

    def test_real_time_is_not_reported_as_simulation_elapsed(self):
        clock = simulation_clock.SimulationClock(8, time.time())
        simulated = clock.monotonic()
        self.assertLess(simulated, 1_000_000_000)
        self.assertLess(simulated, 1)

    def test_late_child_uses_the_shared_wall_anchor(self):
        origin = 1_800_000_000
        with patch('simulation_clock.time.time', return_value=origin + 30):
            clock = simulation_clock.SimulationClock(2, origin)
            self.assertAlmostEqual(clock.time(), origin + 60)


if __name__ == '__main__':
    unittest.main()
