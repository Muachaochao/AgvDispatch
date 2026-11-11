import unittest
from finalize_campaign import render_markdown
class ReportTests(unittest.TestCase):
 def test_failed_saturation_stays_failed_after_all_tasks_complete(self):
  counts=dict(completedTasks=8,taskRows=8,allStepsCompleted=True,exitEvidencePassed=8,exitEvidenceChecks=8,faultedTasks=0,cancelledTasks=0,activeTasks=0,remainingLeases=0,mapNodes=1088,mapEdges=1863,mapResources=1617)
  text=render_markdown(dict(status='failed',message='未覆盖满负荷排队',counts=counts),'','','')
  self.assertIn('**未通过**',text)
  self.assertIn('**8/8**',text)
  self.assertIn('未覆盖满负荷排队',text)
  self.assertNotIn('20/20',text)
  self.assertNotIn('本轮隔离仿真**通过**',text)
 def test_unknown_verdict_is_not_passed(self):
  counts=dict(completedTasks=0,taskRows=1,allStepsCompleted=False,exitEvidencePassed=0,exitEvidenceChecks=0,faultedTasks=0,cancelledTasks=0,activeTasks=1,remainingLeases=1,mapNodes=1,mapEdges=0,mapResources=1)
  self.assertIn('**未完成**',render_markdown(dict(counts=counts),'','',''))
if __name__=='__main__':unittest.main()
