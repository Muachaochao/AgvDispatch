import {test} from 'node:test';
import assert from 'node:assert/strict';
import {taskTimeRange,taskHistoryUrl,emptyTaskHistoryQuery,taskHistoryError,completeTaskSummary,taskStageSegments,demoTaskPage} from '../src/task-history.ts';
import type {TaskRow,TaskListSummary} from '../src/types.ts';
const summary:TaskListSummary={total:73,running:4,waiting:3,completed:55,cancelled:6,failed:4,other:1};
test('full backend summary is independent of first 50 rows and cancelled stays distinct',()=>{
  const result=completeTaskSummary({items:[],totalCount:73,summary});
  assert.deepEqual(result,summary);
  assert.equal(taskStageSegments(result).reduce((n,s)=>n+s.value,0),73);
  assert.equal(taskStageSegments(result).find(s=>s.label==='已取消')?.value,6);
  assert.equal(completeTaskSummary({items:Array(50).fill({}),totalCount:73}),null);
  assert.equal(completeTaskSummary({items:[],totalCount:73,summary:{...summary,total:50}}),null);
});
test('date shortcuts respect local calendar boundaries including year changes',()=>{
  const now=new Date(2026,0,1,14,30);
  assert.deepEqual(taskTimeRange('today',now),{from:'2026-01-01T00:00',to:'2026-01-02T00:00'});
  assert.deepEqual(taskTimeRange('yesterday',now),{from:'2025-12-31T00:00',to:'2026-01-01T00:00'});
  assert.deepEqual(taskTimeRange('week',now),{from:'2025-12-26T00:00',to:'2026-01-02T00:00'});
  assert.deepEqual(taskTimeRange('all',now),{from:'',to:''});
});
test('history URL preserves applied filters and converts local time to ISO instant',()=>{
  const q={...emptyTaskHistoryQuery(),filter:'completed',search:' CNC-001 ',from:'2026-10-28T00:00',to:'2026-10-29T00:00',pageSize:100};
  const u=new URL(taskHistoryUrl(q,2),'http://fixture');
  assert.equal(u.searchParams.get('from'),new Date(q.from).toISOString());
  assert.equal(u.searchParams.get('to'),new Date(q.to).toISOString());
  assert.equal(u.searchParams.get('page'),'2');assert.equal(u.searchParams.get('pageSize'),'100');
  assert.equal(u.searchParams.get('filter'),'Completed');assert.equal(u.searchParams.get('search'),'CNC-001');
  assert.ok(taskHistoryError({...q,to:q.from}));
  assert.ok(taskHistoryError({...q,pageSize:201}));
});
test('empty or full fixture page gives accurate summary; date end is exclusive',()=>{
  const rows=Array.from({length:73},(_,i):TaskRow=>({taskId:'T'+i,stationId:'CNC',vehicleId:null,stage:'Completed',priority:0,
    createdAt:new Date(2026,9,28,0,i).toISOString(),updatedAt:new Date(2026,9,28,0,i).toISOString()}));
  const q=emptyTaskHistoryQuery(),a=demoTaskPage(rows,q,1),b=demoTaskPage(rows,q,2);
  assert.equal(a.items.length,50);assert.equal(b.items.length,23);assert.equal(a.summary?.total,73);
  const range=demoTaskPage(rows,{...q,from:'2026-10-28T00:10',to:'2026-10-28T00:20'},1);
  assert.equal(range.totalCount,10);assert.equal(range.items[0].taskId,'T10');assert.equal(range.items.at(-1)?.taskId,'T19');
  assert.equal(completeTaskSummary({items:[],totalCount:0})?.total,0);
});
