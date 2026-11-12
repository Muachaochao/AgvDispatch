import unittest
from add_safe_stops import add_stops, localize_split_intersections


class SafeStopTests(unittest.TestCase):
    def fixture(self):
        return dict(unit='m', nodes=[dict(id=str(i),x=i*2,y=0,type='Waypoint',enabled=True,
                    resourceIds=[],agvSiteId=str(i),allowTrafficStop=False) for i in range(9)],
                    edges=[dict(id=str(i),**{'from':str(i),'to':str(i+1)},enabled=True,
                    geometryKind='Line') for i in range(8)],resources=[])

    def test_straight_clearance_and_spacing(self):
        original=self.fixture(); result,audit=add_stops(original)
        self.assertGreater(len(audit['addedStops']),0)
        self.assertEqual(result['edges'],original['edges'])
        self.assertFalse(original['nodes'][1]['allowTrafficStop'])
        for stop in audit['addedStops']:
            self.assertGreaterEqual(stop['x'],1.5)
            self.assertLessEqual(stop['x'],14.5)
            self.assertTrue(next(n for n in result['nodes'] if n['id']==stop['id'])['allowTransitThrough'])

    def test_no_stopping_on_junction_or_near_business_dock(self):
        original=self.fixture()
        original['nodes'].append(dict(id='CNC',x=4,y=.5,type='Station',enabled=True,resourceIds=[]))
        original['edges'].append(dict(id='branch',**{'from':'4','to':'CNC'},enabled=True))
        _,audit=add_stops(original)
        self.assertNotIn('4',[s['id'] for s in audit['addedStops']])
        self.assertNotIn('2',[s['id'] for s in audit['addedStops']])

    def test_units_and_envelope_cannot_be_bypassed(self):
        with self.assertRaises(ValueError):add_stops(self.fixture(),clearance=.1)
        with self.assertRaises(ValueError):add_stops(dict(self.fixture(),unit='mm'))

    def test_long_directed_pair_has_one_shared_midpoint(self):
        original=dict(unit='m',nodes=[dict(id='A',x=0,y=0,type='Waypoint',enabled=True,resourceIds=[]),
                        dict(id='B',x=10,y=0,type='Waypoint',enabled=True,resourceIds=[])],resources=[],
                      edges=[dict(id='AB',**{'from':'A','to':'B'},distance=10,resourceIds=['ROAD'],enabled=True),
                             dict(id='BA',**{'from':'B','to':'A'},distance=10,resourceIds=['ROAD'],enabled=True)])
        result,audit=add_stops(original)
        self.assertEqual(len(audit['addedStops']),1)
        self.assertEqual(len(result['edges']),4)
        self.assertTrue(all(e['resourceIds']==['ROAD'] for e in result['edges']))
        self.assertEqual(sum(e['distance'] for e in result['edges']),20)
        self.assertEqual(result['nodes'][:2],original['nodes'])

    def test_half_edge_retains_near_junction_and_shared_road_only(self):
        nodes = dict(A=dict(x=0,y=0), S=dict(x=5,y=0), B=dict(x=10,y=0))
        def junction(x):
            return dict(type='Intersection',boundary=[dict(x=x-.55,y=-.55),dict(x=x+.55,y=-.55),
                                                       dict(x=x+.55,y=.55),dict(x=x-.55,y=.55)])
        resources = dict(JA=junction(0), JB=junction(10), ROAD=dict(type='Corridor'))
        edge = dict(id='second',**{'from':'S','to':'B'}, resourceIds=['JA','JB','ROAD','UNKNOWN'])
        self.assertEqual(localize_split_intersections(edge,nodes,resources),['JB','ROAD','UNKNOWN'])
        edge.update(**{'from':'A','to':'S'})
        self.assertEqual(localize_split_intersections(edge,nodes,resources),['JA','ROAD','UNKNOWN'])

    def test_near_and_invalid_boundaries_are_not_dropped(self):
        nodes = dict(A=dict(x=0,y=0), B=dict(x=2,y=0))
        edge = dict(**{'from':'A','to':'B'},resourceIds=['NEAR','INVALID'])
        resources = dict(NEAR=dict(type='Intersection',boundary=[dict(x=1,y=1),dict(x=2,y=1),dict(x=1,y=2)]),
                         INVALID=dict(type='Intersection',boundary=[dict(x=float('nan'),y=1)]*3))
        self.assertEqual(localize_split_intersections(edge,nodes,resources),['NEAR','INVALID'])


if __name__=='__main__':unittest.main()
