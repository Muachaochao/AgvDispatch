import tempfile,unittest
from pathlib import Path
import ezdxf
from build_full_map import StationPoint,build_graph,apply_dual_lane_topology,check_wait_departure_clearance,build_configuration
from physical_lanes import build_physical_graph


class PhysicalLaneTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory()
        path=Path(cls.temp.name)/'lanes.dxf'
        doc=ezdxf.new();doc.layers.new('管道')
        ys=(-388949.764,-390015.223,-396289.065,-397354.524,-404567.99,-405567.99,
            -410059.58,-411125.04,-417211.19,-418276.65,-424380.18,-425445.63)
        for y in ys:doc.modelspace().add_line((910916.525,y),(1031462.795,y),dxfattribs={'layer':'管道'})
        doc.saveas(path)
        rows=(-391375.898,-394986.878,-398528.416,-402139.397,-408805.822,-412277.919,-415958.341,-419569.321,-423110.86)
        stations=[StationPoint(f'CNC-{i+1:03}',i,1,0,0,970767.85,y) for i,y in enumerate(rows)]
        cls.m,cls.summary=build_physical_graph(stations,path,build_graph,allow_terminal_fixture=True)
        cls.ns={n['id']:n for n in cls.m['nodes']}

    @classmethod
    def tearDownClass(cls):cls.temp.cleanup()

    def test_configuration_preserves_road_speed_without_second_scale(self):
        configuration = build_configuration(self.m, ['CNC-001'], {})
        self.assertEqual(5, len(configuration['vehicles']))
        self.assertTrue(all(v['speedScale'] == 1 for v in configuration['vehicles']))

    def test_parallel_geometry_and_opposite_directions(self):
        self.assertEqual(6,self.summary['physicalLanes']['pairs'])
        self.assertGreaterEqual(self.summary['physicalLanes']['minimumSeparationMeters'],1)
        for e in self.m['edges']:
            a,b=self.ns[e['from']],self.ns[e['to']]
            if e['laneRole']=='eastbound':self.assertGreater(b['x'],a['x'])
            if e['laneRole']=='westbound':self.assertLess(b['x'],a['x'])

    def test_every_crossing_is_a_shared_graph_endpoint(self):
        for e in self.m['edges']:
            a,b=self.ns[e['from']],self.ns[e['to']]
            if abs(a['y']-b['y'])>1e-5:continue
            for other in self.m['edges']:
                c,d=self.ns[other['from']],self.ns[other['to']]
                if abs(c['x']-d['x'])>1e-5:continue
                if min(a['x'],b['x'])+1e-5<c['x']<max(a['x'],b['x'])-1e-5 and min(c['y'],d['y'])-1e-5<=a['y']<=max(c['y'],d['y'])+1e-5:
                    self.fail(f"未切分交叉 {e['id']} / {other['id']}")

    def test_terminal_departures_keep_four_parked_peers_clear(self):
        # The legacy helper asks for CNC-305; use the same point as the last
        # fixture machine, without changing any production graph authority.
        last=next(n for n in self.m['nodes'] if n['agvSiteId']=='CNC-009')
        old=last['id'];last['id']='BUS-CNC-305'
        for e in self.m['edges']:
            for field in ('from','to'):
                if e[field]==old:e[field]=last['id']
        try:self.assertTrue(check_wait_departure_clearance(self.m)['passed'])
        finally:
            for e in self.m['edges']:
                for field in ('from','to'):
                    if e[field]==last['id']:e[field]=old
            last['id']=old

    def test_short_connector_and_duplicate_resource_regression(self):
        self.assertTrue(all(e['distance']>.05 for e in self.m['edges']))
        self.assertEqual(len(self.m['resources']),len({r['id'] for r in self.m['resources']}))
        self.assertEqual(len(self.m['nodes']),len({(n['x'],n['y']) for n in self.m['nodes']}))
        for e in self.m['edges']:
            for nid in (e['from'],e['to']):
                self.assertTrue(set(self.ns[nid]['resourceIds']).issubset(e['resourceIds']))
        self.assertTrue(all(len(r['boundary'])>=4 for r in self.m['resources']))

    def test_machine_approach_nodes_never_become_traffic_stops(self):
        core,_=build_graph([StationPoint(f'CNC-{i+1:03}',i,1,0,0,970767.85,y)
                            for i,y in enumerate((-391375.898,-394986.878,-398528.416,-402139.397,
                                                  -408805.822,-412277.919,-415958.341,-419569.321,-423110.86))])
        apply_dual_lane_topology(core)
        approaches=[n for n in core['nodes'] if n['id'].startswith('APPROACH-')]
        self.assertTrue(approaches)
        self.assertTrue(all(n['type']=='Intersection' and n.get('allowTrafficStop') is False
                            and n.get('allowTransitThrough') is True for n in approaches))

    def test_terminal_uses_bay_centres_not_text_labels(self):
        from terminal_layout import FIXTURE_CENTRES
        for name,point in FIXTURE_CENTRES.items():
            self.assertEqual((self.ns[name]['x'],self.ns[name]['y']),point)
        self.assertNotEqual(self.ns['WAIT-01']['x'],984.4508609652442)
        self.assertLess(self.ns['DUMPER-001']['x'],985.028)
        self.assertNotIn('INSPECT-01',self.ns)
        self.assertFalse(any(e['laneRole']=='inspection-access' for e in self.m['edges']))

    def test_waiting_central_crossing_and_shared_column_hubs(self):
        # Upper/lower symbols differ by millimetres; use one collector junction.
        for upper,lower in [('WAIT-02','WAIT-04'),('WAIT-05','WAIT-03')]:
            def hubs(name):
                return {e['to'] for e in self.m['edges'] if e['from']==name and e['laneRole']=='waiting-bay'}
            self.assertEqual(hubs(upper),hubs(lower))
            self.assertEqual(len(hubs(upper)),1)
        self.assertTrue(any(e['laneRole']=='waiting-collector' for e in self.m['edges']))
        self.assertFalse(any(e['laneRole'] in ('waiting-in','waiting-out') for e in self.m['edges']))

    def test_no_road_centreline_crosses_equipment_platform(self):
        box=self.summary['physicalLanes']['terminalLayout']['platformBounds']
        for e in self.m['edges']:
            a,b=self.ns[e['from']],self.ns[e['to']];lo,hi=0.,1.
            for field,minimum,maximum in [('x',box[0],box[2]),('y',box[1],box[3])]:
                delta=b[field]-a[field]
                if abs(delta)<1e-10:
                    if not minimum<=a[field]<=maximum:hi=-1;break
                else:
                    t0,t1=sorted(((minimum-a[field])/delta,(maximum-a[field])/delta))
                    lo=max(lo,t0);hi=min(hi,t1)
            self.assertGreater(lo,hi,f"道路穿过设备平台: {e['id']}")

    def test_reciprocal_crossovers_share_capacity_one_resource(self):
        pairs={(e['from'],e['to']):e for e in self.m['edges']}
        capacities={r['id']:r['capacity'] for r in self.m['resources']}
        for e in self.m['edges']:
            if e['laneRole'] not in ('crossover','dumper-access'):continue
            reverse=pairs[(e['to'],e['from'])]
            shared=set(e['resourceIds']) & set(reverse['resourceIds'])
            self.assertTrue(any(r.startswith('SEG-') and capacities[r]==1 for r in shared))
        turns=[e for e in self.m['edges'] if e['laneRole']=='terminal-turn']
        self.assertTrue(turns)
        for e in turns:
            self.assertGreater(self.ns[e['to']]['x'],self.ns[e['from']]['x'])
            self.assertNotIn((e['to'],e['from']),pairs)
        outgoing=[e for e in self.m['edges'] if e['from']=='DUMPER-001']
        self.assertEqual(len(outgoing),1)
        self.assertLess(self.ns[outgoing[0]['to']]['x'],self.ns['DUMPER-001']['x'])

    def test_exported_configuration_has_local_hash_and_existing_inspection_reference(self):
        import hashlib,json
        from build_full_map import build_configuration
        c=build_configuration(self.m,['CNC-001'],{})
        expected=hashlib.sha256(json.dumps(self.m,ensure_ascii=False,sort_keys=True,separators=(',',':')).encode()).hexdigest().upper()
        self.assertEqual(c['mapIdentity']['definitionHash'],expected)
        self.assertEqual(c['inspectionSite'],'WAIT-01')
        self.assertIn(c['inspectionSite'],self.ns)

    def test_dumper_admission_protects_shared_return_crossing(self):
        guard=self.summary['physicalLanes']['terminalLayout']['protectedExitJunction']
        shared=set(guard['resourceIds'])
        self.assertTrue(shared)
        self.assertTrue(shared.issubset(self.ns['DUMPER-001']['resourceIds']))
        approach=[e for e in self.m['edges'] if e['to']=='DUMPER-001']
        follower_crossing=[e for e in self.m['edges'] if e['laneRole']=='waiting-collector' and guard['nodeId'] in (e['from'],e['to'])]
        self.assertTrue(approach and follower_crossing)
        self.assertTrue(all(shared.issubset(e['resourceIds']) for e in approach+follower_crossing))

    def test_return_merge_uses_gap_separate_from_entry_rail(self):
        layout=self.summary['physicalLanes']['terminalLayout']
        self.assertGreater(layout['returnMerge']['x']-layout['spineX'][0],2)
        self.assertGreater(layout['returnMerge']['x']-layout['spineX'][1],1)

    def test_missing_cad_symbols_fail_instead_of_using_labels(self):
        from terminal_layout import terminal_layout
        with self.assertRaisesRegex(ValueError,'缺少CAD泊位轮廓'):
            terminal_layout(ezdxf.new())

    def test_missing_middle_crossover_is_detected_as_real_bypass_failure(self):
        from check_parked_bypass import check
        nodes=[];edges=[]
        def node(id,x,y,site=None):
            nodes.append(dict(id=id,x=x,y=y,agvSiteId=site or id,enabled=True,
                type='Station' if site and site.startswith('CNC-') else 'Waypoint',
                resourceIds=[site] if site and site.startswith('CNC-') else [],allowTransitThrough=False))
        for x in (0,2,5,6,10):node(f'U{x}',x,1)
        for x in (0,5,10):node(f'L{x}',x,0)
        node('WAIT-05',10,-1);node('DUMPER-001',10,2)
        node('B',2,1.1,'CNC-209');node('T',6,1.1,'CNC-233')
        def edge(a,b,resources=(),both=False):
            edges.append(dict(id=f'E{len(edges)}',**{'from':a,'to':b},enabled=True,resourceIds=list(resources)))
            if both:edge(b,a,resources)
        for a,b in ((0,2),(2,5),(5,6),(6,10)):edge(f'U{a}',f'U{b}',('CNC-209',) if a<5 else ())
        edge('L10','L5');edge('L5','L0');edge('L0','U0',both=True);edge('U10','L10',both=True)
        edge('B','U2',('CNC-209',),True);edge('T','U6',('CNC-233',),True)
        edge('WAIT-05','L10',both=True);edge('DUMPER-001','U10',both=True)
        fixture=dict(nodes=nodes,edges=edges)
        self.assertFalse(check(fixture)['previousFailure']['passed'])
        edge('L5','U5',both=True)
        result=check(fixture)
        self.assertTrue(result['previousFailure']['passed'])
        self.assertTrue(result['passed'])


if __name__=='__main__':unittest.main()
