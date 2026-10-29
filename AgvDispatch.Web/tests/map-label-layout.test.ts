import {test} from 'node:test';
import assert from 'node:assert/strict';
import {placeMapLabels,type MapLabelInput} from '../src/map-label-layout.ts';
const station=(id:string,x:number,y:number):MapLabelInput=>({id,x,y,width:34,height:18,kind:'station',priority:10});
test('dense station labels stay close and do not overlap in auto mode',()=>{
  const input=Array.from({length:40},(_,i)=>station('LM'+(i+1),200+(i%8)*24,180+Math.floor(i/8)*24));
  const result=placeMapLabels(input,800,600);
  assert.ok(result.length>10&&result.length<40);
  for(const a of result){
    const node=input.find(l=>l.id===a.id)!;
    const dx=Math.max(a.rect.left-node.x,0,node.x-a.rect.right),dy=Math.max(a.rect.top-node.y,0,node.y-a.rect.bottom);
    assert.ok(Math.hypot(dx,dy)<=40);
    assert.ok(!a.leader||a.leader.length<=48);
    for(const b of result.filter(b=>b!==a))assert.ok(!(a.rect.left<b.rect.right&&a.rect.right>b.rect.left&&a.rect.top<b.rect.bottom&&a.rect.bottom>b.rect.top));
  }
});
test('labels do not cover vehicle footprint; vehicle labels get priority',()=>{
  const vehicle:MapLabelInput={...station('AGV',300,240),kind:'vehicle',priority:100,width:62};
  const reserved={left:280,top:220,right:320,bottom:260};
  const result=placeMapLabels([station('LM1',300,240),vehicle],800,600,[reserved]);
  assert.equal(result[0].id,'AGV');
  for(const {rect:r} of result)assert.ok(!(r.left<reserved.right&&r.right>reserved.left&&r.top<reserved.bottom&&r.bottom>reserved.top));
});
test('all mode keeps station identifiers local, hidden mode keeps only vehicle and hovered label',()=>{
  const labels=[station('LM1',200,200),station('LM2',200,200),{...station('AGV',300,300),kind:'vehicle' as const,priority:100}];
  assert.equal(placeMapLabels(labels,600,500,[],'all').length,3);
  assert.deepEqual(placeMapLabels(labels,600,500,[],'none').map(l=>l.id),['AGV']);
  labels[0].focused=true;
  assert.deepEqual(new Set(placeMapLabels(labels,600,500,[],'none').map(l=>l.id)),new Set(['AGV','LM1']));
});
test('layout is deterministic across node source order and rejects invalid positions',()=>{
  const labels=[station('B',200,200),station('A',250,200),station('C',NaN,50)];
  assert.deepEqual(placeMapLabels(labels,600,500),placeMapLabels([...labels].reverse(),600,500));
  assert.equal(placeMapLabels(labels,600,500).length,2);
});
