"""Regression of virtual-device faults; no real hardware or dispatch DB is used."""
import asyncio
import json
from pathlib import Path
import struct
import socket
import sys
import tempfile
import time
import unittest
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'WebSingleVehicleLab'))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'WebTwoVehicleLab'))
from device_twin import DeviceTwin
from device_controls import validate_controls
from multi_twin import Car, SharedPlc, run as run_multi


class Fixture:
    def __init__(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        settings = {'HandshakeExecution': {'PlcContractVersion': 'plc-28-stage-v2'},
                    'Agv': {'Robots': [{'VehicleId': 'A'}, {'VehicleId': 'B'}]}}
        navigation = {'nodes': [dict(id=name, agvSiteId='LM' + str(i), x=i*.2, y=0)
                      for i, name in enumerate(['检修', '倒料', '取货A', '取货B'])],
                      'edges': [dict(id='E0', **{'from': '检修', 'to': '倒料'}, bidirectional=True, speedLimit=1)]}
        (self.root / 'service-settings.json').write_text(json.dumps(settings), encoding='utf-8')
        (self.root / 'navigation.agvmap.json').write_text(json.dumps(navigation), encoding='utf-8')
        self.car = Car(self.root, bytearray(1024), '检修', 'A')

    def control(self, **fields):
        value = dict(sequence=uuid.uuid4().hex, **fields)
        (self.root / 'simulation-control.json').write_text(json.dumps(value), encoding='utf-8')
        self.car.refresh_controls()
        return value['sequence']

    def close(self):
        self.car.journal.close()
        self.temp.cleanup()


class ControlValidationTests(unittest.TestCase):
    def test_custom_vehicle_battery_speed_and_initial_load_are_applied(self):
        f = Fixture()
        try:
            f.car.vehicle_profiles = {'A': dict(initialLoaded=True, batteryPercent=37, speedScale=2.5)}
            f.car.apply_vehicle_profile()
            reply = f.car.robokit_reply(1100, {})
            self.assertTrue(f.car.loaded)
            self.assertEqual(f.car.speed_scale, 2.5)
            self.assertAlmostEqual(reply['battery_level'], .37)
        finally:
            f.close()

    def test_battery_voltage_curve_interpolates_and_charging_is_bounded(self):
        f = Fixture()
        try:
            f.car.voltage_curve = [dict(percent=0,voltage=20),dict(percent=50,voltage=24),dict(percent=100,voltage=26)]
            f.car.charging_gain_percent_per_hour = 10
            f.car.battery_percent = .25
            self.assertEqual(f.car.battery_voltage(), 22)
            f.car.battery_percent = .75
            self.assertEqual(f.car.battery_voltage(), 25)
            f.car.charging = True; f.car.consume_battery(seconds=360)
            self.assertAlmostEqual(f.car.battery_percent, .76)
            f.car.consume_battery(seconds=360000)
            self.assertEqual(f.car.battery_percent, 1)
        finally:
            f.close()

    def test_batch_calls_publish_same_scan_or_reject_whole_batch(self):
        f = Fixture()
        try:
            car = f.car
            # Fixture Car shares caller-provided PLC memory, so set physical box inputs.
            car.setbit(0, 1, True)
            car.setbit(2, 1, True)
            car.apply_external_input(dict(sequence='batch1', calls=['CNC-001', 'CNC-002']))
            self.assertTrue(car.bit(0, 0))
            self.assertTrue(car.bit(2, 0))
            self.assertTrue(car.input_accepted)
            car.setbit(0, 0, False)
            car.setbit(2, 0, False)
            car.setbit(2, 5, True)
            car.apply_external_input(dict(sequence='batch2', calls=['CNC-001', 'CNC-002']))
            self.assertFalse(car.bit(0, 0), 'A blocked second origin must not partially publish the first call')
            self.assertFalse(car.bit(2, 0))
            self.assertFalse(car.input_accepted)
            car.setbit(2, 5, False)
            car.apply_external_input(dict(sequence='legacy', call='CNC-001'))
            self.assertTrue(car.bit(0, 0), 'Legacy single call remains supported')
            self.assertFalse(car.bit(2, 0))
        finally:
            f.close()

    def test_all_or_nothing_validation(self):
        f = Fixture()
        try:
            sequence = f.control(vehicles={'A': {'paused': True, 'speedScale': 2}})
            self.assertTrue(f.car.paused)
            f.control(vehicles={'A': {'paused': False}, 'B': {'speedScale': 0}})
            self.assertEqual(sequence, f.car.control_sequence)
            self.assertTrue(f.car.paused, 'Invalid second vehicle must not partially update first vehicle')
            self.assertIsNotNone(f.car.control_error)
            f.control()
            self.assertFalse(f.car.paused, 'A new complete control document resets omitted controls')
            self.assertEqual(.75, f.car.speed_scale)
            self.assertIsNone(f.car.control_error)
        finally:
            f.close()

    def test_reject_unknown_vehicle_edge_nonfinite_and_bool(self):
        for value in [
            {'vehicles': {'C': {'paused': True}}},
            {'vehicles': {'A': {'blockedEdges': ['missing']}}},
            {'vehicles': {'A': {'speedScale': float('nan')}}},
            {'plc': {'readyDelaySeconds': True}},
            {'plc': {'suppressReady': 1}},
        ]:
            with self.subTest(value=value), self.assertRaises(ValueError):
                validate_controls(dict(sequence='x', **value), {'A', 'B'}, {'E0'})

    def test_delay_suppress_and_fault_are_physical_inputs(self):
        f = Fixture()
        try:
            car = f.car
            f.control(plc={'readyDelaySeconds': 2})
            car.setbit(610, 0, True)
            car.scan_handshakes()
            self.assertFalse(car.bit(610, 2))
            car.ready_requested_at -= 3
            car.scan_handshakes()
            self.assertTrue(car.bit(610, 2))
            f.control(plc={'suppressReady': True, 'suppressDone': True})
            car.scan_handshakes()
            self.assertFalse(car.bit(610, 2))
            car.station = car.site('倒料')
            car.x, car.y = car.nodes[car.station]['x'], car.nodes[car.station]['y']
            car.setbit(610, 1, True)
            car.scan_handshakes()
            car.dump_started -= 6
            car.scan_handshakes()
            self.assertFalse(car.bit(610, 3))
            f.control(plc={'doneDelaySeconds': 0})
            car.scan_handshakes()
            self.assertTrue(car.bit(610, 3))
            f.control(plc={'fault': True})
            car.scan_handshakes()
            self.assertTrue(car.bit(610, 4))
            self.assertFalse(car.bit(610, 3))
            self.assertFalse(car.bit(0, 5), 'Device fault never manufactures upper-computer handshake outputs')
        finally:
            f.close()


class DeviceNetworkTests(unittest.IsolatedAsyncioTestCase):
    async def wait_until(self, predicate, message, timeout=3):
        deadline = asyncio.get_running_loop().time() + timeout
        while not predicate():
            if asyncio.get_running_loop().time() >= deadline:
                self.fail(message)
            await asyncio.sleep(.01)

    async def asyncSetUp(self):
        self.f = Fixture()
        self.server = await asyncio.start_server(self.f.car.robokit, '127.0.0.1', 0)
        self.port = self.server.sockets[0].getsockname()[1]

    async def asyncTearDown(self):
        for writer in tuple(self.f.car.writers):
            writer.close()
            await writer.wait_closed()
        self.server.close()
        await self.server.wait_closed()
        self.f.close()

    async def request(self, api, value):
        reader, writer = await asyncio.open_connection('127.0.0.1', self.port)
        header = bytearray(16)
        header[0] = 0x5a
        payload = json.dumps(value).encode()
        struct.pack_into('>I', header, 4, len(payload))
        struct.pack_into('>H', header, 8, api)
        writer.write(header + payload)
        await writer.drain()
        try:
            response_header = await asyncio.wait_for(reader.readexactly(16), 2)
            return json.loads(await reader.readexactly(struct.unpack_from('>I', response_header, 4)[0]))
        finally:
            writer.close()
            await writer.wait_closed()

    async def test_dropped_reply_still_accepts_original_transport(self):
        self.f.control(vehicles={'A': {'dropNextResponse': True}})
        self.assertIn('x', await self.request(1100, {}), 'Queries must not consume pending command fault')
        command = {'move_task_list': [{'task_id': 'T1', 'source_id': 'LM0', 'id': 'LM1'}]}
        with self.assertRaises(asyncio.IncompleteReadError):
            await self.request(3066, command)
        self.assertEqual(1, self.f.car.tasks['T1'])
        self.assertFalse(self.f.car.drop_next_response)
        self.assertEqual(400, (await self.request(3066, command))['ret_code'])
        status = await self.request(1110, {'task_ids': ['T1']})
        self.assertEqual(1, status['task_status_package']['task_status_list'][0]['status'])

    async def test_offline_closes_current_socket_and_reconnects(self):
        reader, writer = await asyncio.open_connection('127.0.0.1', self.port)
        await self.wait_until(lambda: bool(self.f.car.writers), 'Server did not accept the test connection')
        self.f.control(vehicles={'A': {'offline': True}})
        self.assertEqual(b'', await asyncio.wait_for(reader.read(1), 1))
        writer.close()
        await writer.wait_closed()
        self.f.control()
        self.assertIn('x', await self.request(1100, {}))

    async def test_segment_progress_tracks_distance_and_stops_on_pause(self):
        car = self.f.car
        car.nodes['LM1']['x'] = 2
        self.assertNotIn('percentage', car.robokit_reply(1110, {}))
        self.assertEqual(0, car.robokit_reply(3066, {'move_task_list': [
            {'task_id': 'progress', 'source_id': 'LM0', 'id': 'LM1'}]})['ret_code'])
        driver = asyncio.create_task(car.drive())
        try:
            await self.wait_until(lambda: car.x > .05, 'No physical progress')
            self.f.control(vehicles={'A': {'paused': True}})
            reply = await self.request(1110, {})
            self.assertEqual(('LM0', 'LM1'), (reply['source_name'], reply['target_name']))
            self.assertAlmostEqual(car.x / 2, reply['percentage'])
            self.assertGreater(reply['percentage'], 0)
            self.assertLess(reply['percentage'], 1)
            before = reply['percentage']
            await asyncio.sleep(.25)
            self.assertEqual(before, (await self.request(1110, {}))['percentage'])
            car.robokit_reply(3003, {'taskId': 'progress'})
            await self.wait_until(lambda: car.current is None, 'Cancel did not clear active progress')
            self.assertNotIn('percentage', car.robokit_reply(1110, {}))
        finally:
            driver.cancel()
            await asyncio.gather(driver, return_exceptions=True)

    async def test_near_vehicle_blocks_motion_and_resumes_when_clear(self):
        car = self.f.car
        car.nodes['LM1']['x'] = 5
        peer = Car(self.f.root, car.memory, 'LM1', 'B')
        car.peers = [peer]
        try:
            car.robokit_reply(3066, {'move_task_list': [
                {'task_id': 'yield', 'source_id': 'LM0', 'id': 'LM1'}]})
            driver = asyncio.create_task(car.drive())
            try:
                peer.x = 1.4
                await self.wait_until(lambda: car.blocked_by_vehicle == 'B', 'Missing obstacle stop')
                self.assertTrue(car.robokit_reply(1100, {})['blocked'])
                self.assertGreaterEqual(peer.x-car.x, car.body_radius()+peer.body_radius())
                stopped = car.x
                await asyncio.sleep(.2)
                self.assertEqual(stopped, car.x)
                peer.y = 10
                await self.wait_until(lambda: car.x > stopped and car.blocked_by_vehicle is None, 'Vehicle did not resume after peer cleared')
                self.assertEqual(2, car.tasks['yield'], 'Obstacle release must retain the original task identity')
            finally:
                driver.cancel()
                await asyncio.gather(driver, return_exceptions=True)
        finally:
            peer.journal.close()

    async def test_vehicle_obstacle_signal_stops_and_preserves_original_task(self):
        car = self.f.car
        car.nodes['LM1']['x'] = 4
        self.assertEqual(0, car.robokit_reply(3066, {'move_task_list': [
            {'task_id': 'direct-obstacle', 'source_id': 'LM0', 'id': 'LM1'}]})['ret_code'])
        driver = asyncio.create_task(car.drive())
        try:
            await self.wait_until(lambda: car.current == 'direct-obstacle', 'Driver did not start obstacle task')
            self.f.control(vehicles={'A': {'speedScale': 2}})
            await self.wait_until(lambda: car.x > .05, 'Vehicle did not begin moving')
            self.f.control(vehicles={'A': {'obstacle': True, 'speedScale': 2}})
            await self.wait_until(lambda: car.robokit_reply(1100, {}).get('blocked') is True,
                                  'Vehicle did not report Blocked')
            stopped = car.x
            await asyncio.sleep(.25)
            self.assertEqual(stopped, car.x)
            self.assertEqual(2, car.tasks['direct-obstacle'],
                             'Obstacle must not replace or cancel the original vendor task')
            self.f.control(vehicles={'A': {'obstacle': False}})
            await self.wait_until(lambda: car.x > stopped, 'Vehicle did not resume after obstacle clear')
            self.assertEqual(2, car.tasks['direct-obstacle'])
        finally:
            driver.cancel()
            await asyncio.gather(driver, return_exceptions=True)

    async def test_pause_block_and_cancel_during_jack(self):
        car = self.f.car
        jack_started, release_jack = asyncio.Event(), asyncio.Event()
        original_wait = car.wait_motion
        async def observed_jack(tid, seconds, edge_id=None):
            jack_started.set()
            # Hold the physical lift boundary so scheduler delays cannot let the
            # action finish before cancellation is injected by the test.
            await release_jack.wait()
            return await original_wait(tid, seconds, edge_id)
        car.wait_motion = observed_jack
        self.f.control(vehicles={'A': {'paused': True, 'speedScale': 10}})
        self.assertEqual(0, car.robokit_reply(3066, {'move_task_list': [
            {'task_id': 'T2', 'source_id': 'LM0', 'id': 'LM1', 'operation': 'JackLoad'}]})['ret_code'])
        driver = asyncio.create_task(car.drive())
        try:
            await self.wait_until(lambda: car.current == 'T2', 'Driver did not start the queued transport')
            await asyncio.sleep(.25)
            self.assertEqual(0, car.x)
            self.f.control(vehicles={'A': {'blockedEdges': ['E0'], 'speedScale': 10}})
            await asyncio.sleep(.25)
            self.assertEqual(0, car.x)
            self.f.control(vehicles={'A': {'speedScale': 10}})
            await asyncio.wait_for(jack_started.wait(), 3)
            self.assertEqual(car.site('倒料'), car.station)
            car.robokit_reply(3003, {'taskId': 'T2'})
            release_jack.set()
            await self.wait_until(lambda: car.current is None, 'Cancelled lift did not leave the executing state')
            self.assertEqual(6, car.tasks['T2'], 'Cancellation during lift cannot turn into successful completion')
            self.assertFalse(car.loaded)
        finally:
            release_jack.set()
            driver.cancel()
            await asyncio.gather(driver, return_exceptions=True)

    async def test_cancelled_segment_does_not_teleport_remaining_chain(self):
        car = self.f.car
        car.nodes['LM1']['x'] = 100  # Long edge leaves a wide cancellation window under CPU contention.
        self.assertEqual(0, car.robokit_reply(3066, {'move_task_list': [
            {'task_id': 'out', 'source_id': 'LM0', 'id': 'LM1'},
            {'task_id': 'back', 'source_id': 'LM1', 'id': 'LM0'}]})['ret_code'])
        driver = asyncio.create_task(car.drive())
        try:
            await self.wait_until(lambda: car.x > 0, 'Vehicle never started its first segment')
            self.assertLess(car.x, car.nodes['LM1']['x'])
            x_when_cancelled = car.x
            car.robokit_reply(3003, {'taskId': 'out'})
            await self.wait_until(lambda: car.tasks['back'] != 1, 'Dependent queued segment was not checked')
            self.assertEqual(5, car.tasks['back'])
            self.assertEqual(x_when_cancelled, car.x, 'The cancelled chain cannot jump to its next segment source')
            self.assertEqual(400, car.robokit_reply(3066, {'move_task_list': [
                {'task_id': 'retry', 'source_id': 'LM0', 'id': 'LM1'}]})['ret_code'])
        finally:
            driver.cancel()
            await asyncio.gather(driver, return_exceptions=True)

    async def test_vendor_can_cancel_final_return_segments_without_fake_completion(self):
        car = self.f.car
        car.nodes['LM4'] = dict(id='LM4', agvSiteId='LM4', x=.4, y=0)
        car.edges['LM1', 'LM4'] = (dict(id='E1', **{'from': 'LM1', 'to': 'LM4'}, bidirectional=False, speedLimit=1), False)
        self.f.control(vehicles={'A': {'cancelFinalReturnSegments': 1, 'speedScale': 10}})
        self.assertEqual(0, car.robokit_reply(3066, {'move_task_list': [
            {'task_id': 'return-01', 'source_id': 'LM0', 'id': 'LM1', 'operation': 'Wait'},
            {'task_id': 'return-02', 'source_id': 'LM1', 'id': 'LM4', 'operation': 'JackUnload'}]})['ret_code'])
        driver = asyncio.create_task(car.drive())
        try:
            await self.wait_until(lambda: car.tasks['return-02'] == 6 and car.current is None,
                                  'Final return segment was not cancelled')
            self.assertEqual(4, car.tasks['return-01'])
            self.assertEqual('LM1', car.station, 'Preceding successful segments remain complete')
            self.assertEqual('LM1', car.station, 'Cancelled final segment cannot teleport to destination')
            self.assertEqual(0, car.cancel_final_return_segments)
        finally:
            driver.cancel()
            await asyncio.gather(driver, return_exceptions=True)


class MultiVehicleRuntimeTests(unittest.IsolatedAsyncioTestCase):
    async def test_three_independent_tcp_cars_one_shared_plc(self):
        f = Fixture()
        task = None
        try:
            # Reserve distinct loopback endpoints while collecting the configuration.
            reservations = [socket.socket() for _ in range(7)]
            for sock in reservations:
                sock.bind(('127.0.0.1', 0))
            ports = [sock.getsockname()[1] for sock in reservations]
            robots = [dict(VehicleId=name, IpAddress='127.0.0.1', Port=ports[i*2], NavigationPort=ports[i*2+1])
                      for i, name in enumerate(['A', 'B', 'C'])]
            settings = json.loads((f.root/'service-settings.json').read_text(encoding='utf-8'))
            settings.update(Agv={'Robots': robots}, Plc={'IpAddress': '127.0.0.1'})
            (f.root/'service').mkdir()
            (f.root/'service/appsettings.json').write_text(json.dumps(settings), encoding='utf-8')
            (f.root/'lab.json').write_text(json.dumps(dict(
                multiVehicleSimulation=True, simulationOnly=True, plcPort=ports[6],
                initialSites={'A': 'LM0', 'B': '取货A', 'C': '取货B'})), encoding='utf-8')
            for sock in reservations:
                sock.close()
            task = asyncio.create_task(run_multi(f.root))
            for _ in range(30):
                if (f.root/'multi-state.json').exists():
                    break
                if task.done():
                    task.result()
                await asyncio.sleep(.1)
            state = json.loads((f.root/'multi-state.json').read_text(encoding='utf-8'))
            self.assertEqual({'A', 'B', 'C'}, set(state['cars']))
            self.assertEqual({'LM0', 'LM2', 'LM3'}, {c['station'] for c in state['cars'].values()})
            control = dict(sequence=uuid.uuid4().hex, vehicles={'C': {'offline': True}}, plc={'fault': True})
            (f.root/'simulation-control.json').write_text(json.dumps(control), encoding='utf-8')
            for _ in range(30):
                state = json.loads((f.root/'multi-state.json').read_text(encoding='utf-8'))
                if state['simulationControl']['sequence'] == control['sequence'] and state['cars']['C']['simulationControl']['sequence'] == control['sequence']:
                    break
                await asyncio.sleep(.1)
            self.assertTrue(state['cars']['C']['offline'])
            self.assertFalse(state['cars']['A']['offline'])
            self.assertTrue(bytes.fromhex(state['memory_hex'])[610] & 16)
            self.assertEqual(control['sequence'], state['simulationControl']['sequence'])
        finally:
            (f.root/'STOP').touch()
            if task:
                await asyncio.wait_for(task, 3)
            f.close()


if __name__ == '__main__':
    unittest.main()
