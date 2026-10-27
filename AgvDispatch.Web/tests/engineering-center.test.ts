import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { csvRows, exportCsv, importDiff, newRow, parseCsv } from '../src/engineering-center.ts';

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
