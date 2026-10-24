import {test} from 'node:test';
import assert from 'node:assert/strict';
import {emptyPlan,parsePlan,validateDefinition} from '../src/workbench.ts';
import {annotationLabel} from '../src/area-annotations.ts';
test('manufacturer areas survive plan import/export independently of traffic resources',()=>{
 const p=emptyPlan();p.definition.areaAnnotations=[{id:'A',sourceType:'AreaDescription',text:'CNC工位A区',boundary:[{x:-2,y:-1},{x:1,y:-1},{x:1,y:3},{x:-2,y:3}],strokeColor:'#ff00ff',fillColor:'#ff00ff',fillOpacity:.2,sourceJson:'original'}];
 const reloaded=parsePlan(JSON.stringify(p));assert.deepEqual(reloaded.definition.areaAnnotations,p.definition.areaAnnotations);assert.deepEqual(reloaded.definition.resources,[]);
 assert.deepEqual(annotationLabel(p.definition.areaAnnotations[0]!),{x:-.5,y:3});
});
test('old maps remain readable; invalid area geometry is rejected',()=>{
 const p=emptyPlan();assert.deepEqual(validateDefinition(p.definition),[]);
 p.definition.areaAnnotations=[{id:'bad',sourceType:'AreaDescription',text:'bad',boundary:[{x:NaN,y:0}],strokeColor:'invalid',fillColor:'#ffffff',fillOpacity:2}];
 assert.ok(validateDefinition(p.definition).some(e=>e.includes('区域标注')));
});
