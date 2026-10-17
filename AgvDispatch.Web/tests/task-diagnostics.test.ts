import { test } from 'node:test';
import assert from 'node:assert/strict';
import { boolText, conditionLabel, diagnosticFresh, stepStatusLabel } from '../src/task-diagnostics.ts';
import type { ProgressCondition } from '../src/types.ts';

const condition: ProgressCondition = { ruleId:'PLC.BoxPresent.0', name:'箱体检测', stationId:'TEST', variableName:'BoxPresent', address:'test', expectedValue:false, currentValue:false, quality:'Good', satisfied:true, result:'Pass', reason:'test', observedAt:null, signalVersion:1, role:'执行条件' };
test('诊断过期、缺失或未来时间不显示当前有效通过', () => {
  const now = Date.parse('2026-10-17T00:00:00Z');
  assert.equal(diagnosticFresh(true, new Date(now-1000).toISOString(), now), true);
  for (const timestamp of [undefined, 'bad', new Date(now-10001).toISOString(), new Date(now+2000).toISOString()]) assert.equal(diagnosticFresh(true,timestamp,now),false);
  assert.equal(diagnosticFresh(false,new Date(now).toISOString(),now),false);
  assert.equal(conditionLabel(condition,false),'观察已过期');
});
test('质量未知和null不当成0；复位回读不标成PLC业务接收', () => {
  assert.equal(conditionLabel(condition,true),'满足');
  assert.equal(conditionLabel({...condition,quality:'Stale'},true),'未知');
  assert.equal(conditionLabel({...condition,currentValue:null},true),'未知');
  assert.equal(conditionLabel({...condition,role:'复位目标'},true),'符合目标');
  assert.equal(conditionLabel({...condition,role:'复位目标',satisfied:false},true),'待复位');
  assert.equal(boolText(null),'未知'); assert.equal(boolText(false),'0'); assert.equal(boolText(true),'1');
});

test('Ready是步骤待协调，不能显示成所有PLC条件就绪',()=>{ assert.equal(stepStatusLabel('Ready','条件就绪'),'待协调执行'); });

test('原写入回读符合仍是待人工核对，不显示自动通过',()=>{assert.equal(conditionLabel({...condition,role:'待人工核对写入；不自动放行'},true),'回读符合，仍待人工核对');});
