"""Point-table provenance and multi-station PLC regressions, without field I/O."""
import asyncio
import csv
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'WebSingleVehicleLab'))
from device_twin import DeviceTwin
from station_catalog import (normalize_stations, station_catalog, validate_twin_stations,
                             write_private_signal_table)


SOURCE = Path(__file__).resolve().parents[2] / 'AgvDispatch.Service/config/plc-signals.csv'


class StationCatalogTests(unittest.TestCase):
    def test_current_table_never_claims_inferred_points_are_field_confirmed(self):
        catalog = station_catalog(SOURCE)
        self.assertEqual(len(catalog), 305)
        verified = [row['stationId'] for row in catalog if row['fieldConfirmed']]
        self.assertEqual(verified, ['CNC-001', 'CNC-002'])
        self.assertEqual(catalog[2]['signalSource'], 'isolated-synthetic')
        self.assertIn('现场未确认', catalog[2]['sourceLabel'])
        self.assertEqual(catalog[-1]['offset'], 608)

    def test_extra_station_requires_explicit_synthetic_opt_in(self):
        with self.assertRaisesRegex(ValueError, '尚未现场确认'):
            normalize_stations([dict(stationId='CNC-009', pickupSite='P9')], SOURCE)
        result = normalize_stations([dict(stationId='CNC-009', pickupSite='P9',
                                         signalSource='isolated-synthetic')], SOURCE)
        self.assertEqual(result[0]['offset'], 16)
        self.assertFalse(result[0]['fieldConfirmed'])
        self.assertEqual(normalize_stations(result, SOURCE), result)
        self.assertEqual(result[0]['priority'], 0)
        self.assertEqual(normalize_stations([dict(result[0], priority=100)], SOURCE)[0]['priority'], 100)
        for priority in (-1, 101, True, 0.5):
            with self.subTest(priority=priority), self.assertRaisesRegex(ValueError, '优先级'):
                normalize_stations([dict(result[0], priority=priority)], SOURCE)
        with self.assertRaisesRegex(ValueError, 'PLC偏移'):
            normalize_stations([dict(result[0], offset=5)], SOURCE)
        with self.assertRaises(ValueError):
            normalize_stations([dict(stationId='CNC-999', pickupSite='P9',
                                     signalSource='isolated-synthetic')], SOURCE)

    def test_private_table_selects_all_requested_stations_without_changing_provenance(self):
        source_before = SOURCE.read_bytes()
        stations = [dict(stationId='CNC-001', pickupSite='P1'),
                    dict(stationId='CNC-009', pickupSite='P9', signalSource='isolated-synthetic'),
                    dict(stationId='CNC-305', pickupSite='P305', signalSource='isolated-synthetic')]
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / 'service/config/plc-signals.csv'
            manifest = write_private_signal_table(SOURCE, target, stations)
            self.assertEqual(manifest['signalCount'], 29)
            self.assertTrue(manifest['isolationOnly'])
            with target.open(encoding='utf-8-sig', newline='') as stream:
                rows = list(csv.DictReader(stream))
            self.assertEqual({row['stationId'] for row in rows},
                             {'CNC-001', 'CNC-009', 'CNC-305', 'DUMPER-001'})
            synthetic = [row for row in rows if row['stationId'] == 'CNC-009']
            self.assertEqual(len(synthetic), 8)
            self.assertTrue(all(row['enabled'] == 'true' for row in synthetic))
            self.assertTrue(all(row['addressStatus'] == 'Inferred' for row in synthetic))
            self.assertTrue(all('现场未确认' in row['sourceNote'] for row in synthetic))
            self.assertFalse(target.with_name(target.name + '.tmp').exists())
        self.assertEqual(SOURCE.read_bytes(), source_before)
        with self.assertRaisesRegex(ValueError, '不得覆盖'):
            write_private_signal_table(SOURCE, SOURCE, stations)

    def test_table_with_colliding_or_malformed_station_addresses_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / 'bad.csv'
            original = SOURCE.read_text(encoding='utf-8-sig')
            for old, new in [('DB1.DBX2.', 'DB1.DBX0.'), ('DB1.DBX2.1', 'DB1.DBX2.0'),
                             ('DB1.DBX2.0', 'DB2.DBX2.0'), ('DB1.DBX2.', 'DB1.DBX610.')]:
                target.write_text(original.replace(old, new), encoding='utf-8')
                with self.subTest(address=new), self.assertRaises(ValueError):
                    station_catalog(target)

    def test_topology_rejects_shared_dumper_offsets_duplicates_and_invalid_ranges(self):
        station = dict(stationId='A', pickupSite='P1', offset=608)
        self.assertEqual(validate_twin_stations([station]), [station])
        for value in (-1, 1024, 610, True, 1.2):
            with self.subTest(offset=value), self.assertRaises(ValueError):
                validate_twin_stations([dict(station, offset=value)])
        with self.assertRaises(ValueError):
            validate_twin_stations([station, dict(station, stationId='B', pickupSite='P2')])
        with self.assertRaises(ValueError):
            validate_twin_stations([station, dict(station, stationId='B', offset=607)])

    def test_all_documented_stations_can_be_explicitly_selected_without_changing_field_points(self):
        stations = [dict(stationId=entry['stationId'], pickupSite='P'+str(index),
                         signalSource=entry['signalSource'])
                    for index, entry in enumerate(station_catalog(SOURCE))]
        normalized = normalize_stations(stations, SOURCE)
        self.assertEqual(len(normalized), 305)
        self.assertEqual(len({station['offset'] for station in normalized}), 305)
        self.assertEqual(len(validate_twin_stations(normalized)), 305)


class ExpandedPlcTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.stations = [dict(stationId='CNC-001', pickupSite='P1', offset=0),
                         dict(stationId='CNC-009', pickupSite='P9', offset=16,
                              signalSource='isolated-synthetic'),
                         dict(stationId='CNC-305', pickupSite='P305', offset=608,
                              signalSource='isolated-synthetic')]
        (self.root / 'lab.json').write_text(json.dumps(dict(simulationTopology=dict(stations=self.stations))), encoding='utf-8')
        (self.root / 'service-settings.json').write_text(json.dumps(dict(
            HandshakeExecution=dict(PlcContractVersion='plc-28-stage-v2'))), encoding='utf-8')
        (self.root / 'navigation.agvmap.json').write_text(json.dumps(dict(nodes=[
            dict(id=name, agvSiteId='LM'+str(index), x=index, y=0)
            for index, name in enumerate(('检修', '倒料', 'P1', 'P9', 'P305'))], edges=[])), encoding='utf-8')
        self.twin = DeviceTwin(self.root)
        self.addCleanup(self.twin.journal.close)

    def test_three_nonconsecutive_stations_call_acknowledge_and_repeat_independently(self):
        twin = self.twin
        ids = [station['stationId'] for station in self.stations]
        twin.apply_external_input(dict(sequence='first', calls=ids))
        self.assertTrue(twin.input_accepted)
        self.assertTrue(all(twin.bit(offset, 0) for offset in (0, 16, 608)))
        twin.setbit(16, 4, True)
        twin.scan_handshakes()
        self.assertFalse(twin.bit(16, 0))
        self.assertTrue(twin.bit(0, 0))
        self.assertTrue(twin.bit(608, 0))
        twin.setbit(16, 4, False)
        twin.apply_external_input(dict(sequence='repeat-nine', calls=['CNC-009']))
        self.assertTrue(twin.input_accepted)
        states = {station['stationId']: station for station in twin.station_snapshot()}
        self.assertTrue(states['CNC-009']['cycleActive'])
        self.assertEqual(states['CNC-305']['offset'], 608)

    def test_busy_station_rejects_entire_large_batch_without_partial_calls(self):
        twin = self.twin
        twin.setbit(608, 6, True)
        twin.apply_external_input(dict(sequence='busy', calls=[station['stationId'] for station in self.stations]))
        self.assertFalse(twin.input_accepted)
        self.assertTrue(all(not twin.bit(offset, 0) for offset in (0, 16, 608)))

    def test_duplicate_inflight_call_is_rejected_instead_of_counting_a_second_cycle(self):
        self.twin.apply_external_input(dict(sequence='first', call='CNC-009'))
        self.twin.apply_external_input(dict(sequence='second', call='CNC-009'))
        self.assertFalse(self.twin.input_accepted)
        self.assertTrue(self.twin.bit(16, 0))

    def test_saved_plc_evidence_cannot_silently_shrink_or_extend_shared_memory(self):
        state = dict(tasks={}, station='LM0', x=0, y=0, angle=0, loaded=False,
                     completed_segments=0, contract='plc-28-stage-v2')
        for values in (dict(memory_hex='00' * 3), dict(memory_hex='00' * 1025),
                       dict(db1=[10, 0, 10], dumper=[0]), dict(db1=[10, 0, 10, 0], dumper=[])):
            (self.root / 'device-state.json').write_text(json.dumps(dict(state, **values)), encoding='utf-8')
            with self.subTest(values=values), patch('device_twin.DeviceJournal'), self.assertRaisesRegex(ValueError, 'memory size'):
                DeviceTwin(self.root)

    @staticmethod
    def item(byte, bit=0, kind=1, count=1):
        return (b'\x12\x0a\x10' + bytes([kind]) + struct.pack('>HH', count, 1) + b'\x84' +
                (byte * 8 + bit).to_bytes(3, 'big'))

    def test_s7_multiple_separated_bytes_preserve_other_stations_and_dumper(self):
        twin = self.twin
        param = b'\x05\x02' + self.item(16, 4) + self.item(608, 4)
        packet = bytes(17) + param + b'\x00\x03\x00\x01\x01\x00\x00\x03\x00\x01\x01'
        before = bytes(twin.memory)
        self.assertEqual(twin.s7_memory_data(packet, param, 5), b'\xff\xff')
        self.assertTrue(twin.bit(16, 4))
        self.assertTrue(twin.bit(608, 4))
        self.assertEqual(twin.memory[0], before[0])
        self.assertEqual(twin.memory[610], before[610])
        read_param = b'\x04\x02' + self.item(16, kind=2) + self.item(608, kind=2)
        response = twin.s7_memory_data(bytes(17) + read_param, read_param, 4)
        self.assertEqual(response, b'\xff\x04\x00\x08\x1a\x00\xff\x04\x00\x08\x1a')

    def test_bad_second_s7_write_cannot_resize_memory_or_partially_write_first(self):
        twin = self.twin
        for byte, kind, count, payload in [(1024, 1, 1, b'\x00\x03\x00\x01\x01'),
                                           (608, 2, 1, b'\x00\x04\x00\x10\x01\x02'),
                                           (608, 1, 1, b'\x00\x03\x00\x01')]:
            param = b'\x05\x02' + self.item(16, 4) + self.item(byte, kind=kind, count=count)
            packet = bytes(17) + param + b'\x00\x03\x00\x01\x01\x00' + payload
            before = bytes(twin.memory)
            with self.subTest(byte=byte, payload=payload), self.assertRaises(ValueError):
                twin.s7_memory_data(packet, param, 5)
            self.assertEqual(bytes(twin.memory), before)
            self.assertEqual(len(twin.memory), 1024)

    def test_real_loopback_s7_request_reads_and_writes_separated_stations(self):
        async def exchange():
            server = await asyncio.start_server(self.twin.s7, '127.0.0.1', 0)
            reader, writer = await asyncio.open_connection('127.0.0.1', server.sockets[0].getsockname()[1])

            async def request(param, payload=b''):
                s7 = b'\x32\x01\x00\x00\x00\x01' + struct.pack('>HH', len(param), len(payload)) + param + payload
                frame = b'\x02\xf0\x80' + s7
                writer.write(b'\x03\x00' + struct.pack('>H', len(frame) + 4) + frame)
                await writer.drain()
                header = await asyncio.wait_for(reader.readexactly(4), 2)
                return header + await asyncio.wait_for(reader.readexactly(int.from_bytes(header[2:], 'big') - 4), 2)

            try:
                param = b'\x05\x02' + self.item(16, 4) + self.item(608, 4)
                answer = await request(param, b'\x00\x03\x00\x01\x01\x00\x00\x03\x00\x01\x01')
                self.assertEqual(answer[19:], b'\x05\x02\xff\xff')
                param = b'\x04\x02' + self.item(16, kind=2) + self.item(608, kind=2)
                answer = await request(param)
                self.assertEqual(answer[19:], b'\x04\x02\xff\x04\x00\x08\x1a\x00\xff\x04\x00\x08\x1a')
                self.assertEqual(len(self.twin.memory), 1024)
                self.assertEqual(self.twin.memory[610], 0)
            finally:
                writer.close()
                await writer.wait_closed()
                server.close()
                await server.wait_closed()
                await asyncio.sleep(0)
        asyncio.run(exchange())


if __name__ == '__main__':
    unittest.main()
