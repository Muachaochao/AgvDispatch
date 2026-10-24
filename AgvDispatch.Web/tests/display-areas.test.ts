import {test} from 'node:test';
import assert from 'node:assert/strict';
import {containsPoint,rectangle,suggestedStations,appliesToMap} from '../src/display-areas.ts';
import type {WebMap} from '../src/types.ts';
const map:WebMap={id:'A',version:'V1',definitionHash:'hash',name:'test',coordinateSystem:'RobotWorld',unit:'m',nodes:[{id:'B1',agvSiteId:'LM1',x:1,y:1,enabled:true,type:'Station'},{id:'B2',agvSiteId:'LM2',x:5,y:5,enabled:true,type:'Station'}],edges:[],resources:[],warnings:[]};
test('reversed rectangle gesture normalizes bounds and includes edge stations',()=>{const r=rectangle({x:3,y:3},{x:1,y:1});assert.equal(containsPoint(r,{x:1,y:1}),true);assert.deepEqual(suggestedStations(r,map),['B1']);assert.equal(containsPoint(r,{x:5,y:5}),false);});
test('concave polygon does not include points in its notch',()=>{const b=[{x:0,y:0},{x:4,y:0},{x:4,y:1},{x:1,y:1},{x:1,y:4},{x:0,y:4}];assert.equal(containsPoint(b,{x:.5,y:3}),true);assert.equal(containsPoint(b,{x:3,y:3}),false);});
test('map changes suppress display areas until rechecked',()=>{const c={map,basisHash:'b',document:null,applicable:true};assert.equal(appliesToMap(c,map),true);assert.equal(appliesToMap(c,{...map,id:'B'}),false);assert.equal(appliesToMap(c,{...map,version:'V2'}),false);assert.equal(appliesToMap({...c,applicable:false},map),false);assert.equal(appliesToMap(c,{...map,definitionHash:'changed'}),false);});
