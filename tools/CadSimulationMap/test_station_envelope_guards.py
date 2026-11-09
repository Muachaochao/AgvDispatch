import unittest
from build_full_map import apply_station_envelope_guards


class StationEnvelopeGuardTests(unittest.TestCase):
    def test_parked_vehicle_reserves_nearby_through_lane_without_edge_crossing(self):
        definition = dict(nodes=[
            dict(id='S', agvSiteId='CNC-172', x=0., y=-.636),
            dict(id='A', x=-3., y=0.), dict(id='B', x=3., y=0.),
            dict(id='C', x=-3., y=2.), dict(id='D', x=3., y=2.)],
            edges=[dict(id='NEAR', **{'from': 'A', 'to': 'B'}, resourceIds=['GUARD']),
                   dict(id='FAR', **{'from': 'C', 'to': 'D'}, resourceIds=[])])
        summary = apply_station_envelope_guards(definition)
        self.assertEqual(definition['edges'][0]['resourceIds'], ['GUARD', 'CNC-172'])
        self.assertEqual(definition['edges'][1]['resourceIds'], [])
        self.assertEqual(summary['addedEdgeResourceBindings'], 1)
        self.assertEqual(apply_station_envelope_guards(definition)['addedEdgeResourceBindings'], 0)


if __name__ == '__main__': unittest.main()
