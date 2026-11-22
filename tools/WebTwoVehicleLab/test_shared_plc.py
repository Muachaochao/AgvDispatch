"""Model boundary tests; these are not mechanical or production PLC acceptance."""
import json
import tempfile
import time
import unittest
from pathlib import Path

from multi_twin import Car, SharedPlc
from simulation_clock import CLOCK


class SharedPlcTests(unittest.TestCase):
    def test_one_memory_and_current_equipment_cycle(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            settings = {'HandshakeExecution': {'PlcContractVersion': 'plc-28-stage-v2'}}
            navigation = {'nodes': [dict(id=name, agvSiteId='LM' + str(i), x=i, y=0)
                for i,name in enumerate(['检修','倒料','取货A','取货B'])], 'edges': []}
            def prepare(path):
                path.mkdir(exist_ok=True)
                (path/'service-settings.json').write_text(json.dumps(settings), encoding='utf-8')
                (path/'navigation.agvmap.json').write_text(json.dumps(navigation), encoding='utf-8')
            prepare(root)
            plc = SharedPlc(root, [])
            for name in ('a','b'):
                prepare(root/name)
            a = Car(root/'a', plc.memory, '取货A')
            b = Car(root/'b', plc.memory, '取货B')
            plc.cars = [a,b]
            self.assertIs(a.memory, b.memory)
            plc.setbit(610,0,True)
            plc.scan_handshakes()
            self.assertTrue(plc.bit(610,2))
            plc.setbit(610,0,False)
            plc.scan_handshakes()
            self.assertFalse(plc.bit(610,2))
            self.assertTrue(plc.dumper_reserved)
            plc.setbit(610,1,True)
            plc.scan_handshakes()
            self.assertIsNone(plc.dump_started, 'Request alone cannot simulate physical arrival')
            b.station = b.site('倒料')
            plc.scan_handshakes()
            self.assertIsNone(plc.dump_started, 'Stale station text without physical arrival is not arrival')
            b.x, b.y = b.nodes[b.station]['x'], b.nodes[b.station]['y']
            plc.scan_handshakes()
            self.assertIsNotNone(plc.dump_started, 'Either independent car can reach the shared machine')
            # The shared PLC times dumping on the isolated business clock, not the process monotonic clock.
            plc.dump_started = CLOCK.monotonic() - 6
            plc.scan_handshakes()
            self.assertTrue(plc.bit(610,3))
            self.assertFalse(plc.bit(0,5), 'The equipment must not invent origin notifications')
            plc.setbit(610,1,False)
            plc.scan_handshakes()
            self.assertFalse(plc.bit(610,3))
            self.assertFalse(plc.dumper_reserved)
            for device in (plc,a,b):
                device.journal.close()


if __name__ == '__main__':
    unittest.main()
