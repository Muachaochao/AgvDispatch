import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { createStandardTemplate, csvRows, exportCsv, importDiff, newRow, normalizeTemplate, parseCsv, templateExport } from '../src/engineering-center.ts';

describe('engineering bulk configuration',()=>{
 it('round trips Chinese, comma, quote, CRLF and arrays',()=>{
  const row={...newRow('stations'),id:'CNC-300',name:'工位,"A"\r\n二号',priority:70,enabled:false};
  assert.deepEqual(csvRows('stations',exportCsv('stations',[row])),[row]);
  const pool={...newRow('pools'),id:'POOL-A',vehicleIds:['AGV-01','AGV-05']};
  assert.deepEqual(csvRows('pools',exportCsv('pools',[pool])),[pool]);
 });
 it('requires unambiguous headers and valid typed cells',()=>{
  assert.throws(()=>csvRows('stations','id,id\nA,A'),/列名/);
  assert.throws(()=>csvRows('stations',exportCsv('stations',[{...newRow('stations'),id:'A',priority:NaN}])),/数字/);
  assert.throws(()=>csvRows('stations',exportCsv('stations',[{...newRow('stations'),id:'A',enabled:'yes'}])),/true/);
  assert.throws(()=>csvRows('stations',exportCsv('stations',[{...newRow('stations'),id:'A'},{...newRow('stations'),id:'a'}])),/重复/);
 });
 it('rejects malformed quote boundaries',()=>{
  assert.throws(()=>parseCsv('id\n"unclosed'),/未闭合/);
  assert.throws(()=>parseCsv('id\n"ok"junk'),/额外/);
  assert.deepEqual(parseCsv('\uFEFFid,name\r\nA,"line\nline"'),[['id','name'],['A','line\nline']]);
 });
 it('merges only imported identifiers and ignores property order',()=>{
  const old=[{id:'A',name:'原'},{id:'B',name:'保留'}];
  const result=importDiff(old,[{name:'原',id:'A'},{id:'C',name:'新增'}]);
  assert.equal(result.added,1);assert.equal(result.changed,0);assert.equal(result.unchanged,1);
  assert.equal(result.merged.length,3);assert.ok(result.merged.some(x=>x.id==='B'));
  assert.equal(importDiff(old,[{id:'A',name:'改'}]).changed,1);
 });
});

describe('engineering task templates',()=>{
 it('defines the fixed durable nine-step contract',()=>{
  const template=createStandardTemplate('BOX-A','取送货','Jack',3);
  assert.equal(template.engine,'DurableNineStepV1');
  assert.equal(template.inputs.length,6);
  assert.equal(template.outputs.length,6);
  assert.equal(template.nodes.length,12);
  assert.equal(template.variables.length,17);
  assert.ok(template.connections.some(x=>x.fromNodeId==='go-to-pickup'&&x.output==='arrival'&&x.toNodeId==='pickup-full-box'));
  assert.equal(template.guards.length,5);
  assert.deepEqual(template.nodes.slice(0,6).map(x=>x.block),['SelectAgv','AcquireTraffic','GoToPickup','PickupFullBox','DeliverFullBoxToDumper','WaitForDumperReady']);
  assert.equal(template.nodes.at(-1)?.block,'ReleaseTraffic');
  assert.ok(template.nodes.every(x=>x.inputs.length&&x.condition));
  assert.ok(template.nodes.filter(x=>!['AcquireTraffic','ReleaseTraffic'].includes(x.block)).every(x=>x.outputs.length));
  assert.ok(template.nodes.every(x=>!x.autoRetry&&x.timeoutSeconds===0));
 });
 it('upgrades legacy template rows and keeps immutable identity in exports',()=>{
  const legacy={id:'LEGACY',name:'旧模板',capability:'Jack',pickupSeconds:4,dumpSeconds:8,returnSeconds:6};
  const normalized=normalizeTemplate(legacy);
  assert.equal(normalized.version,1);
  assert.equal(normalized.nodes.length,12);
  assert.equal(normalized.variables.length,17);
  assert.equal(normalized.guards.length,5);
  const exported=templateExport(normalized);
  assert.equal(exported.schemaVersion,1);
  assert.equal(exported.template.id,'LEGACY');
  assert.equal(exported.template.nodes.length,12);
 });
});
