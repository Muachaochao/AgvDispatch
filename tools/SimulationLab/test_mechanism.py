import asyncio
import unittest
from test_device_controls import Fixture

class MechanismTests(unittest.IsolatedAsyncioTestCase):
    async def test_action_has_no_limit_evidence_until_completed_and_cancel_stops_it(self):
        fixture = Fixture()
        car = fixture.car
        car.tasks['jack'] = 2
        car.operation_seconds = 10
        action = asyncio.create_task(car.execute_mechanism('jack', 'JackLoad'))
        try:
            await asyncio.sleep(0)
            self.assertEqual(car.mechanism['state'], 'Executing')
            self.assertIsNone(car.mechanism['upperLimit'])
            self.assertIsNone(car.mechanism['lowerLimit'])
            car.tasks['jack'] = 6
            self.assertFalse(await asyncio.wait_for(action, timeout=1))
            self.assertEqual(car.mechanism['state'], 'Interrupted')
            self.assertIsNone(car.mechanism['upperLimit'])
        finally:
            action.cancel()
            await asyncio.gather(action, return_exceptions=True)
            fixture.close()

    async def test_completed_action_exposes_limits_and_own_task(self):
        fixture = Fixture()
        try:
            car=fixture.car
            car.tasks['jack']=2
            car.operation_seconds=0
            self.assertTrue(await car.execute_mechanism('jack','JackLoad'))
            self.assertEqual(car.mechanism['state'],'Completed')
            self.assertTrue(car.mechanism['upperLimit'])
            self.assertFalse(car.mechanism['lowerLimit'])
            self.assertEqual(car.mechanism['taskId'],'jack')
        finally: fixture.close()
    async def test_cancelled_action_has_no_completion_or_limit_evidence(self):
        fixture=Fixture()
        try:
            car=fixture.car
            car.tasks['jack']=6
            self.assertFalse(await car.execute_mechanism('jack','JackUnload'))
            self.assertEqual(car.mechanism['state'],'Interrupted')
            self.assertIsNone(car.mechanism['lowerLimit'])
        finally: fixture.close()
