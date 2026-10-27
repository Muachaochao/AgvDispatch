import test from 'node:test';
import assert from 'node:assert/strict';
import { businessChoices, standbyChoices } from '../src/business-run-options.ts';
import type { CreationOptions } from '../src/types.ts';
const options = {
 stations: [
  {stationId:'LATHE-037',allowedVehicleIds:['CARRIER-007','CARRIER-009']},
  {stationId:'MILL-WEST',allowedVehicleIds:['CARRIER-007']},
  {stationId:'PRESS-9',allowedVehicleIds:['CARRIER-007','CARRIER-011']}
 ],
 vehicles: Array.from({length:7},(_,i)=>({vehicleId:'CARRIER-'+String(i+7).padStart(3,'0')})),
 standbys:[
  {id:'NORTH',site:'PARK-NORTH',allowedVehicleIds:['CARRIER-007'],priority:10},
  {id:'SOUTH',site:'PARK-SOUTH',allowedVehicleIds:['CARRIER-007'],priority:80},
  {id:'OTHER',site:'PARK-OTHER',allowedVehicleIds:['CARRIER-009'],priority:99}
 ]
} as unknown as CreationOptions;
test('custom three-station run intersects configured vehicle pools',()=>{
 const r=businessChoices(options,['PRESS-9','LATHE-037','MILL-WEST']);
 assert.equal(r.valid,true);assert.deepEqual(r.vehicles.map(v=>v.vehicleId),['CARRIER-007']);
 assert.equal(businessChoices(options,['LATHE-037']).vehicles.length,2);
});
test('unconfigured, duplicate and empty selections are invalid',()=>{
 for(const ids of [[],['MISSING'],['LATHE-037','LATHE-037']])assert.equal(businessChoices(options,ids).valid,false);
});
test('standbys follow selected vehicle and configured priority',()=>{
 assert.deepEqual(standbyChoices(options,'CARRIER-007').map(s=>s.site),['PARK-SOUTH','PARK-NORTH']);
 assert.deepEqual(standbyChoices(options,'CARRIER-009').map(s=>s.id),['OTHER']);
 assert.deepEqual(standbyChoices(options,'MISSING'),[]);
});
test('legacy options without pool restrictions remain usable',()=>{
 const legacy={stations:[{stationId:'CUSTOM'}],vehicles:options.vehicles} as CreationOptions;
 assert.equal(businessChoices(legacy,['CUSTOM']).vehicles.length,7);
 assert.deepEqual(standbyChoices(legacy,'CARRIER-007'),[]);
});
