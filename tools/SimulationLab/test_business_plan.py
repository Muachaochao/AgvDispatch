import unittest
from business_plan import generate_plan
from scenarios import validate_scenario
class PlanValidationTests(unittest.TestCase):
 def test_zero_interval_repeated_station_remains_a_queued_business_call(self):
  plan=generate_plan(dict(seed=4,count=2,intervalSeconds=0,stationIds=['CNC-001']),[dict(stationId='CNC-001')])
  scenario=validate_scenario(dict(name='business-calls',calls=plan['calls']))
  self.assertEqual(len(scenario['calls']),2)
 def test_rejects_invalid_parameters(self):
  valid=dict(seed=4,count=6,intervalSeconds=3,stationIds=['CNC-001'])
  for change in [dict(seed=True),dict(count=0),dict(intervalSeconds=float('nan')),dict(stationIds=[]),dict(stationIds=['CNC-999']),dict(stationIds=['CNC-001','CNC-001'])]:
   with self.assertRaises(ValueError): generate_plan(dict(valid,**change),[dict(stationId='CNC-001')])
 def test_station_order_does_not_change_calls(self):
  valid=dict(seed=4,count=10,intervalSeconds=3,stationIds=['CNC-001','CNC-002'])
  stations=[dict(stationId=s) for s in valid['stationIds']]
  self.assertEqual(generate_plan(valid,stations),generate_plan(dict(valid,stationIds=list(reversed(valid['stationIds']))),stations))
