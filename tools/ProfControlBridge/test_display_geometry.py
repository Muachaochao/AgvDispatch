import unittest

from configure_display import geometry_check


class DisplayGeometryTest(unittest.TestCase):
    def check(self, stations, lines):
        return geometry_check(dict(stations=[dict(mark=str(i), position=dict(x=x, y=y))
                                             for i, (x, y) in enumerate(stations)],
                                   lines=[dict(p1=dict(x=a, y=b), p2=dict(x=c, y=d))
                                          for a, b, c, d in lines]))

    def test_connected_endpoints_pass(self):
        self.assertTrue(self.check([(0, 0), (1, 0)], [(0, 0, 1, 0)])['passed'])

    def test_duplicate_station_is_rejected(self):
        self.assertEqual(len(self.check([(0, 0), (.005, .005)], [])['overlappingStations']), 1)

    def test_near_unconnected_endpoint_is_rejected(self):
        result = self.check([(0, 0)], [(.015, 0, 1, 0)])
        self.assertFalse(result['passed'])
        self.assertEqual(result['nearUnconnected'][0]['connected'], 0)

    def test_distant_endpoint_is_not_near(self):
        self.assertTrue(self.check([(0, 0)], [(.03, 0, 1, 0)])['passed'])


if __name__ == '__main__':
    unittest.main()
