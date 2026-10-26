// Isolated mock HTTP + Chromium only. Never connects to 5199, PLC, or a vehicle.
import {createServer} from 'node:http';
import {spawn} from 'node:child_process';
import {existsSync,mkdirSync,readFileSync,writeFileSync} from 'node:fs';
import {resolve,dirname,sep} from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';
const project=resolve(dirname(fileURLToPath(import.meta.url)),'../..'),dist=resolve(project,'AgvDispatch.Web/dist');
const profile=resolve(project,'.codex-tmp',`task-recovery-browser-${Date.now()}`);mkdirSync(profile,{recursive:true});
const binary=['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe','C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert.ok(binary);let child,ws,seq=0;const pending=new Map(),mutations=[],errors=[];
let recovery={sessionId:'original-session',released:false,recoveryEnabled:true,message:'原任务需要核对',items:[{ticketId:'ticket-original',taskId:'TASK-original',stepId:'step-original',stepText:'返箱确认',state:'Pending',reason:'核对载荷与原周期',verified:false,revision:7,vehicleSummary:'原车辆 AGV-01',plcSummary:'原PLC周期',resourceSummary:'保留原占用',identitySummary:'原车任务身份',identityBlocksRecovery:false,loadSummary:'须现场核对载荷'}],audits:[]};
const run={runId:'original-run',plan:{requestId:'original-start',mode:'Cycle',vehicleId:'AGV-01',stations:['CNC-001'],rounds:3,standbySite:'LM7'},stage:'Hold',message:'车辆定位未就绪',completedJobs:0,taskId:'TASK-original',stopRequested:true,updatedAt:new Date().toISOString()};
const server=createServer(async(req,res)=>{try{
 const url=new URL(req.url,'http://127.0.0.1');const json=(code,data)=>{res.writeHead(code,{'Content-Type':'application/json'});res.end(JSON.stringify(data));};
 if(req.method==='POST'){let raw='';for await(const p of req)raw+=p;mutations.push({path:url.pathname,body:JSON.parse(raw)});if(url.pathname==='/api/recovery/decisions'){recovery={...recovery,items:recovery.items.map(i=>({...i,verified:true,revision:8}))};return json(200,{accepted:true,message:'核对已记录'});}if(url.pathname==='/api/recovery/release'){recovery={...recovery,released:true};return json(200,{accepted:true,message:'后台核验放行'});}if(url.pathname==='/api/tasks')return json(200,{accepted:true,taskId:'TASK-original',message:'原任务已受理'});return json(400,{error:'unexpected mutation'});}
 if(url.pathname==='/api/recovery')return json(200,recovery);
 if(url.pathname==='/api/business-run')return json(200,{current:run});
 if(url.pathname==='/api/task-options')return json(200,{available:false,mapId:'M',mapVersionId:'V',parameterRevision:'R',waitingSite:'LM5',dumpingSite:'LM6',vehicles:[{vehicleId:'AGV-01',label:'AGV-01',available:false,explanation:'未启用业务执行'}],stations:['CNC-001','CNC-002'].map(stationId=>({stationId,pickupSite:stationId==='CNC-001'?'LM2':'LM4',available:false,configurationValid:false,explanation:`第 1 步（前往取货点）：PLC ${stationId}/Ack：所属 PLC 写入开关未开启；第 9 步（结束 PLC 握手）：PLC ${stationId}/Ack：所属 PLC 写入开关未开启`}))});
 if(url.pathname==='/api/config')return json(200,{taskSubmissionEnabled:true,taskCancellationEnabled:true});
 if(url.pathname.startsWith('/api/'))return json(503,{error:'isolated fixture only'});
 const file=resolve(dist,'.'+(url.pathname==='/'?'/index.html':url.pathname));if(!file.startsWith(dist+sep)||!existsSync(file)){res.writeHead(404);return res.end();}
 res.writeHead(200,{'Content-Type':file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':'text/html'});res.end(readFileSync(file));
}catch(e){errors.push(String(e));res.writeHead(500);res.end('{}');}});
await new Promise(ok=>server.listen(0,'127.0.0.1',ok));
const sleep=ms=>new Promise(ok=>setTimeout(ok,ms));
async function wait(fn,label){const end=Date.now()+20000;while(Date.now()<end){if(await fn())return;await sleep(100);}throw new Error(label);}
function rpc(method,params={}){return new Promise((ok,no)=>{const id=++seq,t=setTimeout(()=>no(new Error(method+' timeout')),15000);pending.set(id,{ok:v=>{clearTimeout(t);ok(v);},no});ws.send(JSON.stringify({id,method,params}));});}
async function evaluate(expression){const r=await rpc('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(JSON.stringify(r.exceptionDetails));return r.result.value;}
async function click(text){await wait(()=>evaluate(`(()=>{const b=[...document.querySelectorAll('button')].find(e=>e.textContent.trim()===${JSON.stringify(text)});if(!b||b.disabled)return false;b.click();return true;})()`),'button '+text);}
async function fresh(){await evaluate(`window.mockOnline=true`);await sleep(300);}
function check(label,pass){assert.ok(pass,label);console.log('PASS '+label);}
try{
 child=spawn(binary,['--headless=new','--disable-gpu','--no-first-run','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{windowsHide:true,stdio:'ignore'});
 await wait(()=>existsSync(resolve(profile,'DevToolsActivePort')),'browser');const port=readFileSync(resolve(profile,'DevToolsActivePort'),'utf8').split('\n')[0];
 const pages=await(await fetch(`http://127.0.0.1:${port}/json/list`)).json();ws=new WebSocket(pages.find(p=>p.type==='page').webSocketDebuggerUrl);await new Promise(ok=>ws.addEventListener('open',ok,{once:true}));
 ws.addEventListener('message',e=>{const r=JSON.parse(e.data);if(r.id){const p=pending.get(r.id);if(p){pending.delete(r.id);r.error?p.no(new Error(JSON.stringify(r.error))):p.ok(r.result);}}});
 await rpc('Page.enable');await rpc('Runtime.enable');
 await rpc('Emulation.setDeviceMetricsOverride',{width:1440,height:1000,deviceScaleFactor:1,mobile:false});
 await rpc('Page.addScriptToEvaluateOnNewDocument',{source:`window.mockOnline=false;window.WebSocket=class { constructor(){this.timer=setInterval(()=>this.onmessage?.({data:JSON.stringify({source:'backend',connected:window.mockOnline,receivedAt:new Date().toISOString(),tasks:{items:[],totalCount:0},status:{agvs:[],safety:{dispatchingEnabled:false}},devices:{equipment:[]},resources:[],alarms:[]})}),100)} close(){clearInterval(this.timer)} };`});
 const base=`http://127.0.0.1:${server.address().port}`;
 await rpc('Page.navigate',{url:base+'/#/recovery?taskId=TASK-original'});
 await wait(()=>evaluate(`Boolean(document.querySelector('.recovery-form'))`),'recovery page');
 check('Reading the recovery page never mutates original task',mutations.length===0);
 check('Offline observations cannot submit recovery',await evaluate(`[...document.querySelectorAll('.recovery-form button')].every(b=>b.disabled)`));
 await fresh();await click('读取 / 刷新恢复批次');
 await evaluate(`(()=>{const t=document.querySelector('.recovery-form textarea');t.value='已现场核对车辆载荷和原PLC周期';t.dispatchEvent(new Event('input',{bubbles:true}));const i=document.querySelector('.recovery-form input:not([type=checkbox])');i.value='现场确认空载';i.dispatchEvent(new Event('input',{bubbles:true}));})()`);
 check('Review still requires three explicit physical confirmations',await evaluate(`[...document.querySelectorAll('button')].find(b=>b.textContent==='提交所选条目核对').disabled`));
 await evaluate(`document.querySelectorAll('.recovery-form input[type=checkbox]').forEach((e,i)=>{if(i<3)e.click()})`);await click('提交所选条目核对');await wait(()=>mutations.length===1,'review sent');
 check('Review preserves original session, ticket and revision',mutations[0].body.sessionId==='original-session'&&mutations[0].body.ticketId==='ticket-original'&&mutations[0].body.revision===7);
 await wait(()=>evaluate(`document.querySelector('.recovery-panel select').textContent.includes('已核对')`),'verified refresh');
 check('Refreshing clears prior release confirmation',await evaluate(`[...document.querySelectorAll('button')].find(b=>b.textContent==='请求后台核验并放行本批次').disabled`));
 const recoveryShot=await rpc('Page.captureScreenshot',{format:'png'});writeFileSync(resolve(profile,'recovery-review.png'),Buffer.from(recoveryShot.data,'base64'));
 await evaluate(`document.querySelectorAll('.recovery-form input[type=checkbox]')[4].click()`);await click('请求后台核验并放行本批次');await wait(()=>mutations.length===2,'release');
 check('Release targets the original batch',mutations[1].body.sessionId==='original-session');
 await rpc('Page.navigate',{url:base+'/#/tasks'});await wait(()=>evaluate(`Boolean(document.querySelector('.management-table'))`),'task list');
 check('Task list stays focused without duplicate run controls',await evaluate(`!document.querySelector('.business-run')&&!document.body.textContent.includes('业务任务执行取箱')`));
 const runShot=await rpc('Page.captureScreenshot',{format:'png',captureBeyondViewport:true});writeFileSync(resolve(profile,'business-hold.png'),Buffer.from(runShot.data,'base64'));
 await fresh();await click('创建任务');await click('正常按需运行');
 await wait(()=>evaluate(`document.querySelector('.run-status')?.textContent.includes('original-run')`),'run remains in creation dialog');
 check('Original Hold and recovery operations remain inside creation dialog',await evaluate(`document.querySelector('.run-status').textContent.includes('暂停待核对')&&document.querySelector('.run-status a').getAttribute('href').includes('TASK-original')`));
 check('Repeated write permission blocks show one reason with collapsed technical details',await evaluate(`document.querySelector('.business-run section[role=status] strong').textContent==='执行权限尚未开启'&&document.querySelectorAll('.business-run section[role=status]>ul>li').length===1&&!document.querySelector('.business-run details').open`));
 await fresh();await evaluate(`sessionStorage.setItem('agv-web-pending-request',JSON.stringify({requestId:'original-uncertain-request',stationId:'CNC-001',requestedVehicleId:'AGV-01',priority:50,mapId:'M',mapVersionId:'V',parameterRevision:'R'}))`);
 await rpc('Page.reload');await wait(()=>evaluate(`Boolean(document.querySelector('.management-table'))`),'reload');await fresh();await click('创建任务');
 await click('重试原申请');await wait(()=>mutations.length===3,'original retry');
 check('Reload retries the original creation request ID',mutations[2].body.requestId==='original-uncertain-request');
 const shot=await rpc('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});writeFileSync(resolve(profile,'task-recovery.png'),Buffer.from(shot.data,'base64'));
 assert.deepEqual(errors,[]);console.log('PASS isolated recovery browser checks. Evidence: '+profile);
}finally{ws?.close();child?.kill();server.closeAllConnections();await new Promise(ok=>server.close(ok));}
