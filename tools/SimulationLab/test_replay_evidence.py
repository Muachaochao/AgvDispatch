import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

from controller import write
from simulation_reports import replay


class ReplayEvidenceTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.identity = 'a' * 32
        self.run = self.root / ('simulation-run-' + self.identity)
        self.run.mkdir()
        self.runtime = SimpleNamespace(root=self.root)
        run_lookup = patch('simulation_reports.find_run', return_value=self.run)
        run_lookup.start()
        self.addCleanup(run_lookup.stop)
        write(self.run / 'report.json', dict(id=self.identity, finishedAt=150))

    def observations(self, number):
        (self.run / 'observations.jsonl').write_text(''.join(
            json.dumps(dict(time=i, device=dict(cars={}), tasks={}, leases=[])) + '\n'
            for i in range(number)), encoding='utf-8')

    def test_replay_correlates_device_and_service_evidence_in_business_time(self):
        write(self.run / 'report.json', dict(id=self.identity, startedAt=104, finishedAt=120,
            simulationRate=2, simulationOriginUnixMs=100000))
        write(self.run / 'all-task-details.json', {'T': dict(task=dict(taskId='T', stationId='CNC-1',
            vehicleId='AGV-01', createdAt=105), commands=[
                dict(commandId='outbox', vendorCommandId='vendor', vehicleId='AGV-01', sentAt=106),
                dict(commandId='missing', vendorCommandId='other', vehicleId='AGV-01', sentAt=107)],
            executionTimeline=[dict(id='assigned', occurredAt=105.5, kind='Dispatch', title='已派车')])})
        rows = [
            dict(time=101, kind='movement-start', task_id='vendor'),
            dict(time=103.5, kind='robokit', api=3066, request={'move_task_list': [{'task_id':'vendor'}]},
                 response={'ret_code':0}),
            dict(time=104, kind='movement-start', task_id='vendor'),
            dict(time=105, kind='plc-bit', address='DB1.DBX2.0', before=False, value=True),
            dict(time=106, kind='movement-complete', task_id='vendor', station='CNC-1'),
        ]
        (self.root / 'device-events.jsonl').write_text(
            ''.join(json.dumps(row) + '\n' for row in rows), encoding='utf-8')
        def sample(stamp, wait=False, held=False):
            return dict(time=stamp, device=dict(cars={'AGV-01': dict(x=stamp, y=2, angle=0.5)}), tasks={'sampleMode':'bounded-large-batch','total':900},
                trafficWaitRequests=[dict(TaskId='T', VehicleId='AGV-01', ResourceIds='["R"]')] if wait else [],
                leases=[dict(ResourceId='R', TaskId='holder', VehicleId='AGV-02')] if held else [])
        (self.run / 'observations.jsonl').write_text(''.join(json.dumps(row) + '\n' for row in
            [sample(104), sample(106), sample(109, True, True), sample(120)]), encoding='utf-8')
        with patch('simulation_reports.MAX_REPLAY_FRAMES', 2):
            value = replay(self.runtime, self.identity)
        events = value['events']
        self.assertEqual([e['time'] for e in events], sorted(e['time'] for e in events))
        accepted = next(e for e in events if e['kind'] == 'device-accepted')
        self.assertEqual((accepted['time'], accepted['wallTime'], accepted['taskId'], accepted['commandId']),
                         (107, 103.5, 'T', 'outbox'))
        movement = next(e for e in events if e['kind'] == 'movement-start')
        self.assertEqual(movement['time'], 108)
        self.assertEqual(movement['position'], dict(time=106, x=106, y=2, angle=0.5))
        self.assertIn('missing', value['evidence']['unconfirmedCommands'])
        self.assertEqual(next(e for e in events if e['kind'] == 'plc-bit')['detail']['before'], False)
        self.assertEqual(next(e for e in events if e['kind'] == 'traffic-wait')['holderVehicleIds'], ['AGV-02'])
        self.assertTrue(any(e['kind'] == 'traffic-release' for e in events))
        self.assertNotIn(109, [f['time'] for f in value['timeline']])

    def test_old_replay_without_device_clock_keeps_samples_and_reports_gap(self):
        self.observations(2)
        value = replay(self.runtime, self.identity)
        self.assertEqual(len(value['timeline']), 2)
        self.assertFalse(value['evidence']['clockAligned'])
        self.assertTrue(value['evidence']['warnings'])

    def test_audited_release_and_recovery_keep_identity_source_and_time(self):
        write(self.run / 'report.json', dict(id=self.identity, startedAt=100, finishedAt=140,
            simulationRate=2, simulationOriginUnixMs=90000))
        self.observations(0)
        write(self.run / 'replay-traffic-lifecycle.json', [
            dict(EventId='lifecycle', TaskId='T', VehicleId='AGV-02', Kind='Requested',
                 Reason='等待前车驶出', ResourceIds='["road"]', businessTime=110)])
        write(self.run / 'replay-release-audit.json', [
            dict(AuditId='audit', TaskId='T', VehicleId='AGV-02', ResourceId='road',
                 ReleasedAtUtc='1970-01-01 00:02:00', EvidenceHash='hash')])
        write(self.run / 'replay-recovery-audit.json', [
            dict(TaskId='T', VehicleId='AGV-02', OriginalVendorTaskId='old',
                 NewVendorTaskId='new', businessTime=130)])
        result = replay(self.runtime, self.identity)
        lifecycle, release, recovery = [x for x in result['events']]
        self.assertEqual((lifecycle['kind'], lifecycle['resourceIds'], lifecycle['detail']['Reason']),
                         ('traffic-log-requested', ['road'], '等待前车驶出'))
        self.assertEqual((release['kind'], release['time'], release['wallTime']),
                         ('traffic-release-audited', 120, 105))
        self.assertEqual((recovery['kind'], recovery['detail']['OriginalVendorTaskId'],
                          recovery['vendorCommandId']), ('recovery-send-intent', 'old', 'new'))

