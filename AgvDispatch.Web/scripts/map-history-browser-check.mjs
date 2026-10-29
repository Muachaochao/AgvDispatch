// UI regression on a private mock host. The only field request is GET /api/snapshot.
import {createServer} from 'node:http';
import {spawn} from 'node:child_process';
import {existsSync,mkdirSync,readFileSync,writeFileSync} from 'node:fs';
import {resolve,dirname,sep} from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';
const project=resolve(dirname(fileURLToPath(import.meta.url)),'../..'),dist=resolve(project,'AgvDispatch.Web/dist');
const evidence=resolve(project,'artifacts/map-history-browser'),profile=resolve(project,'.codex-tmp','map-history-'+Date.now());
mkdirSync(evidence,{recursive:true});mkdirSync(profile,{recursive:true});
const binary=['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe','C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert.ok(binary,'Chromium required');
const snapshot=await(await fetch('http://127.0.0.1:5199/api/snapshot')).json();
assert.ok(snapshot.map?.nodes.length,'Read-only map fixture missing');
const rows=Array.from({length:73},(_,i)=>({taskId:'TASK-UI-'+String(i).padStart(3,'0'),stationId:'CNC-'+(i%7+1),vehicleId:'AGV-0'+(i%3+1),
 stage:i<55?'Completed':i<61?'Cancelled':i<64?'WaitingForVehicle':i<68?'GoingToPickup':'Faulted',priority:0,
 createdAt:new Date(2026,9,28,0,i).toISOString(),updatedAt:new Date(2026,9,28,0,i+5).toISOString(),errorMessage:null}));
const summary=items=>({total:items.length,running:items.filter(t=>t.stage==='GoingToPickup').length,waiting:items.filter(t=>t.stage==='WaitingForVehicle').length,
 completed:items.filter(t=>t.stage==='Completed').length,cancelled:items.filter(t=>t.stage==='Cancelled').length,failed:items.filter(t=>t.stage==='Faulted').length,other:0});
snapshot.status??={};snapshot.status.agvs=['AGV-01','AGV-02','AGV-03'].map((vehicleId,i)=>{
 const n=snapshot.map.nodes.find(n=>n.agvSiteId==='LM'+(29+i));
 return{vehicleId,displayName:'UI fixture',connection:'Online',quality:'Good',batteryPercent:80,currentStation:n.agvSiteId,x:n.x,y:n.y,angle:0,
 localizationConfidence:.99,mapPositionState:'Normal',mapPositionMessage:'synthetic only',lastSuccessfulPollAt:new Date().toISOString(),updatedAt:new Date().toISOString(),blocked:false,emergencyStopped:false};
});
snapshot.positionedVehicleIds=snapshot.status.agvs.map(v=>v.vehicleId);
snapshot.tasks={items:rows.slice(0,50),totalCount:73,summary:summary(rows)};
snapshot.alarms=[];snapshot.traffic={capturedAt:new Date().toISOString(),requests:[],requestsTruncated:false};
let child,ws,seq=0;const pending=new Map(),requests=[],errors=[];
const server=createServer(async(req,res)=>{
 try{
  const u=new URL(req.url,'http://fixture');
  const json=(code,body)=>{res.writeHead(code,{'Content-Type':'application/json'});res.end(JSON.stringify(body));};
  if(u.pathname==='/api/config')return json(200,{taskSubmissionEnabled:false});
  if(u.pathname==='/api/tasks'){
   requests.push(Object.fromEntries(u.searchParams));
   const from=u.searchParams.get('from'),to=u.searchParams.get('to'),filter=u.searchParams.get('filter')?.toLowerCase(),search=u.searchParams.get('search')??'';
   const filtered=rows.filter(t=>(!from||Date.parse(t.createdAt)>=Date.parse(from))&&(!to||Date.parse(t.createdAt)<Date.parse(to))&&
     (!filter||filter==='all'||filter==='completed'&&t.stage==='Completed'||filter==='cancelled'&&t.stage==='Cancelled')&&
     `${t.taskId} ${t.stationId} ${t.vehicleId}`.includes(search));
   const page=Number(u.searchParams.get('page')??1),pageSize=Number(u.searchParams.get('pageSize')??50);
   return json(200,{items:filtered.slice((page-1)*pageSize,page*pageSize),totalCount:filtered.length,page,pageSize,summary:summary(filtered)});
  }
  if(u.pathname.startsWith('/api/tasks/'))return json(200,null);
  if(u.pathname==='/api/traffic/waiting')return json(200,{page:{capturedAt:new Date().toISOString(),items:[],totalMatched:0,totalWaiting:0,pageNumber:1,pageSize:25,blockingResources:[]}});
  if(u.pathname.endsWith('/display-areas'))return json(200,{map:snapshot.map,applicable:true,document:{areas:[]}});
  if(u.pathname.startsWith('/api/'))return json(200,{});
  const f=resolve(dist,'.'+(u.pathname==='/'?'/index.html':u.pathname));
  if(!f.startsWith(dist+sep)||!existsSync(f)){res.writeHead(404);return res.end();}
  res.writeHead(200,{'Content-Type':f.endsWith('.js')?'text/javascript':f.endsWith('.css')?'text/css':'text/html'});res.end(readFileSync(f));
 }catch(e){errors.push(String(e));res.writeHead(500);res.end('{}');}
});
await new Promise(ok=>server.listen(0,'127.0.0.1',ok));
const sleep=ms=>new Promise(ok=>setTimeout(ok,ms));
async function wait(fn,label){const end=Date.now()+25000;while(Date.now()<end){if(await fn())return;await sleep(100);}throw Error(label);}
function rpc(method,params={}){return new Promise((ok,no)=>{const id=++seq,t=setTimeout(()=>no(Error(method+' timeout')),15000);pending.set(id,{ok:v=>{clearTimeout(t);ok(v);},no});ws.send(JSON.stringify({id,method,params}));});}
async function evaluate(expression){const r=await rpc('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw Error(JSON.stringify(r.exceptionDetails));return r.result.value;}
async function screenshot(name,selector){
 let clip;
 if(selector)clip=await evaluate(`(()=>{const r=document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect();return{x:r.x,y:r.y,width:r.width,height:r.height,scale:1}})()`);
 const result=await rpc('Page.captureScreenshot',{format:'png',...(clip?{clip}:{})});writeFileSync(resolve(evidence,name),Buffer.from(result.data,'base64'));
}
async function click(text,selector='button'){await evaluate(`(()=>{const e=[...document.querySelectorAll(${JSON.stringify(selector)})].find(b=>b.textContent.trim()===${JSON.stringify(text)});if(!e||e.disabled)throw Error('Button unavailable');e.click()})()`);}
async function select(selector,value){await evaluate(`(()=>{const e=document.querySelector(${JSON.stringify(selector)});e.value=${JSON.stringify(value)};e.dispatchEvent(new Event('change',{bubbles:true}))})()`);await sleep(150);}
try{
 child=spawn(binary,['--headless=new','--disable-gpu','--enable-webgl','--no-first-run','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{windowsHide:true,stdio:'ignore'});
 await wait(()=>existsSync(resolve(profile,'DevToolsActivePort')),'browser');
 const port=readFileSync(resolve(profile,'DevToolsActivePort'),'utf8').split('\n')[0],tabs=await(await fetch(`http://127.0.0.1:${port}/json/list`)).json();
 ws=new WebSocket(tabs.find(t=>t.type==='page').webSocketDebuggerUrl);await new Promise(ok=>ws.addEventListener('open',ok,{once:true}));
 ws.addEventListener('message',e=>{const r=JSON.parse(e.data);if(r.id){const p=pending.get(r.id);if(p){pending.delete(r.id);r.error?p.no(Error(JSON.stringify(r.error))):p.ok(r.result);}}});
 await rpc('Page.enable');await rpc('Runtime.enable');await rpc('Emulation.setDeviceMetricsOverride',{width:1600,height:1100,deviceScaleFactor:1,mobile:false});
 await rpc('Page.addScriptToEvaluateOnNewDocument',{source:`const fixture=${JSON.stringify(snapshot)};window.WebSocket=class{constructor(){this.timer=setInterval(()=>{fixture.connected=true;fixture.source='backend';fixture.receivedAt=new Date().toISOString();fixture.status.agvs.forEach(v=>v.lastSuccessfulPollAt=fixture.receivedAt);this.onmessage?.({data:JSON.stringify(fixture)})},500)}close(){clearInterval(this.timer)}};`});
 await rpc('Page.navigate',{url:`http://127.0.0.1:${server.address().port}/`});
 await wait(()=>evaluate(`document.querySelectorAll('.scene-map-label.station:not([hidden])').length>5`),'map labels');
 await sleep(900);await screenshot('overview-2d.png');
 const auto=await evaluate(`({stations:document.querySelectorAll('.scene-map-label.station:not(.vendor-area):not([hidden])').length,maxLeader:Math.max(0,...[...document.querySelectorAll('.scene-label-leader:not([hidden])')].map(e=>parseFloat(e.style.width))),chart:document.querySelector('.ring-chart').getAttribute('aria-label')})`);
 assert.ok(auto.maxLeader<=48);assert.match(auto.chart,/73/);assert.match(auto.chart,/已取消6/);
 await select('select[aria-label="站点标签"]','none');
 assert.equal(await evaluate(`document.querySelectorAll('.scene-map-label.station:not([hidden])').length`),0);
 assert.equal(await evaluate(`document.querySelectorAll('.scene-map-label.vehicle:not([hidden])').length`),3);
 await select('select[aria-label="站点标签"]','all');
 assert.equal(await evaluate(`document.querySelectorAll('.scene-map-label.station:not(.vendor-area):not([hidden])').length`),40);
 await select('select[aria-label="站点标签"]','auto');await click('3D');await sleep(800);await screenshot('overview-3d.png');
 assert.ok(await evaluate(`Math.max(0,...[...document.querySelectorAll('.scene-label-leader:not([hidden])')].map(e=>parseFloat(e.style.width)))<=48`));
 console.log('PASS compact 2D/3D labels, <=48px leaders, all40/hidden0 modes, all73-task chart with separate cancellation.');
 await evaluate(`location.hash='/tasks'`);await wait(()=>evaluate(`document.querySelector('.task-page-header')?.textContent.includes('73 条记录')`),'history total');
 assert.equal(await evaluate(`document.querySelectorAll('.management-table tbody tr').length`),50);
 await click('下一页');await wait(()=>evaluate(`document.querySelectorAll('.management-table tbody tr').length===23`),'second page');
 await select('select[aria-label="每页任务条数"]','100');await wait(()=>evaluate(`document.querySelectorAll('.management-table tbody tr').length===73`),'100 row page');
 await evaluate(`for(const [name,value] of [['创建时间从','2026-10-28T00:10'],['创建时间至','2026-10-28T00:20']]){const e=document.querySelector('input[aria-label="'+name+'"]');e.value=value;e.dispatchEvent(new Event('input',{bubbles:true}))}`);
 await click('查询');await wait(()=>evaluate(`document.querySelector('.task-page-header').textContent.includes('10 条记录')`),'time query');
 const rangeRequest=requests.at(-1);assert.equal(new Date(rangeRequest.from).getHours(),0);assert.equal(new Date(rangeRequest.from).getMinutes(),10);
 await screenshot('task-history-range.png');
 await click('重置');await wait(()=>evaluate(`document.querySelector('.task-page-header').textContent.includes('73 条记录')`),'reset');
 await evaluate(`const e=document.querySelector('input[aria-label="创建时间从"]');e.value='2026-10-29T00:00';e.dispatchEvent(new Event('input',{bubbles:true}))`);
 await click('下一页');await wait(()=>evaluate(`document.querySelectorAll('.management-table tbody tr').length===23`),'applied query');assert.equal(requests.at(-1).from,undefined);
 await screenshot('task-history-page2.png');
 await click('查询');await wait(()=>evaluate(`document.querySelector('.task-page-header').textContent.includes('0 条记录')`),'empty range');
 assert.equal(await evaluate(`document.querySelectorAll('.management-table tbody tr').length`),0);
 console.log('PASS 73 records, second page23, page size100, date filtering10, unapplied draft preserved during paging, zero-result history.');
 assert.deepEqual(errors,[]);writeFileSync(resolve(evidence,'results.json'),JSON.stringify({passed:true,map:auto,requests},null,2));console.log('Evidence: '+evidence);
}finally{ws?.close();child?.kill();server.closeAllConnections();await new Promise(ok=>server.close(ok));}
