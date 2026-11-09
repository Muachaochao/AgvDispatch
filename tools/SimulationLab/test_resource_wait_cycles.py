import json
import unittest

from scenarios import resource_wait_cycles


class ResourceWaitCycleTests(unittest.TestCase):
    def fixture(self, size=2):
        cars = {str(i): {'moving': False} for i in range(size)}
        owners = [dict(TaskId='T'+v, VehicleId=v) for v in cars]
        leases = [dict(ResourceId='R'+v, TaskId='T'+v, VehicleId=v) for v in cars]
        requests = [dict(TaskId='T'+v, VehicleId=v, State='Waiting',
                         ResourceIds=json.dumps(['R'+str((int(v)+1) % size)])) for v in cars]
        return requests, leases, owners, {l['ResourceId']: 1 for l in leases}, cars

    def test_real_lease_cycles_of_two_and_three_stopped_vehicles(self):
        for size in (2, 3):
            self.assertEqual(resource_wait_cycles(*self.fixture(size)), {tuple(str(i) for i in range(size))})

    def test_dispatch_candidate_is_not_an_assigned_owner(self):
        args = self.fixture()
        args[0][0]['TaskId'] = 'UNASSIGNED-CANDIDATE'
        self.assertEqual(resource_wait_cycles(*args), set())

    def test_released_lease_breaks_historical_wait(self):
        args = self.fixture()
        args[1].pop()
        self.assertEqual(resource_wait_cycles(*args), set())

    def test_spare_capacity_does_not_form_a_cycle(self):
        args = self.fixture()
        args[3]['R0'] = 2
        self.assertEqual(resource_wait_cycles(*args), set())

    def test_motion_breaks_persistent_stopped_cycle(self):
        args = self.fixture()
        args[4]['0']['moving'] = True
        self.assertEqual(resource_wait_cycles(*args), set())

    def test_multi_capacity_owner_outside_wait_set_can_release_a_slot(self):
        args = self.fixture()
        args[1].append(dict(ResourceId='R1', TaskId='MOVING', VehicleId='OUTSIDE'))
        args[3]['R1'] = 2
        args[4]['OUTSIDE'] = {'moving': True}
        self.assertEqual(resource_wait_cycles(*args), set())

    def test_multi_capacity_all_owners_closed_in_wait_set_is_deadlock(self):
        args = self.fixture()
        args[1].append(dict(ResourceId='R1', TaskId='T2', VehicleId='2'))
        args[2].append(dict(TaskId='T2', VehicleId='2'))
        args[3]['R1'] = 2
        args[4]['2'] = {'moving': False}
        args[0].append(dict(TaskId='T2', VehicleId='2', State='Waiting', ResourceIds='["R0"]'))
        self.assertEqual(resource_wait_cycles(*args), {('0', '1'), ('0', '2')})

    def test_physical_block_and_resource_wait_form_a_mixed_cycle(self):
        args = self.fixture()
        args[0].pop(0)
        args[4]['0']['blockedByVehicle'] = '1'
        self.assertEqual(resource_wait_cycles(*args), {('0', '1')})

    def test_physically_blocked_car_can_wait_for_a_progressing_owner(self):
        args = self.fixture()
        args[0].pop(0)
        args[4]['0']['blockedByVehicle'] = '1'
        args[4]['1']['moving'] = True
        self.assertEqual(resource_wait_cycles(*args), set())

    def test_one_sided_queue_is_not_deadlock(self):
        args = self.fixture()
        args[0].pop()
        self.assertEqual(resource_wait_cycles(*args), set())


if __name__ == '__main__':
    unittest.main()
