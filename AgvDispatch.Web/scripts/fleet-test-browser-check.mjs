// Isolated mock HTTP + Chromium. Never connects to 5199, a PLC or a vehicle.
import {createServer} from 'node:http';
import {spawn} from 'node:child_process';
import {existsSync,mkdirSync,readFileSync} from 'node:fs';
import {resolve,dirname,sep} from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';

const project=resolve(dirname(fileURLToPath(import.meta.url)),'../..'),dist=resolve(project,'AgvDispatch.Web/dist');
const profile=resolve(project,'.codex-tmp',`fleet-test-browser-${Date.now()}`);mkdirSync(profile,{recursive:true});
const binary=['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe','C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert.ok(binary,'Chromium browser required');
let current=null,queryOffline=false,acceptStart=true,child,ws,seq=0;
const rpcPending=new Map(),starts=[],errors=[];
const sleep=ms=>new Promise(ok=>setTimeout(ok,ms));
const status=()=>({enabled:true,allowedVehicles:['AGV-01','AGV-02','AGV-03'],configuredStations:Array.from({length:7},(_,i)=>'CNC-'+String(i+1).padStart(3,'0')),maxConcurrentTasks:3,current,
 vehicles:['AGV-01','AGV-02','AGV-03'].map((vehicleId,i)=>({vehicleId,site:'LM'+(29+i),fresh:true,message:'状态数据有效'})),tasks:[],observedAt:new Date().toISOString()});
const server=createServer(async(req,res)=>{try{
 const url=new URL(req.url,'http://127.0.0.1');
 const json=(code,data)=>{res.writeHead(code,{'Content-Type':'application/json'});res.end(JSON.stringify(data));};
 if(url.pathname==='/api/config')return json(200,{taskSubmissionEnabled:true});
 if(url.pathname==='/api/fleet-test')return queryOffline?json(503,{detail:'isolated query offline'}):json(200,status());
 if(url.pathname==='/api/fleet-test/start'){
  let raw='';for await(const p of req)raw+=p;const plan=JSON.parse(raw);starts.push(plan);
  if(acceptStart)current={runId:'mock-run-'+starts.length,plan,stage:'Running',message:'模拟后台已经受理',stopRequested:false,updatedAt:new Date().toISOString(),holds:[],standbyReturns:[],calls:plan.stationIds.map(stationId=>({stationId,released:false,taskId:null}))};
  return json(504,{detail:'isolated start lost reply'});
 }
 if(url.pathname.startsWith('/api/'))return json(503,{error:'isolated fixture only'});
 const file=resolve(dist,'.'+(url.pathname==='/'?'/index.html':url.pathname));
 if(!file.startsWith(dist+sep)||!existsSync(file)){res.writeHead(404);return res.end();}
 res.writeHead(200,{'Content-Type':file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':'text/html'});res.end(readFileSync(file));
}catch(e){errors.push(String(e));res.writeHead(500);res.end('{}');}});
await new Promise(ok=>server.listen(0,'127.0.0.1',ok));
async function wait(fn,label){const end=Date.now()+20000;while(Date.now()<end){if(await fn())return;await sleep(100);}throw new Error(label);}
function rpc(method,params={}){return new Promise((ok,no)=>{const id=++seq,t=setTimeout(()=>no(new Error(method+' timeout')),15000);rpcPending.set(id,{ok:v=>{clearTimeout(t);ok(v);},no});ws.send(JSON.stringify({id,method,params}));});}
async function evaluate(expression){const r=await rpc('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(JSON.stringify(r.exceptionDetails));return r.result.value;}
async function click(text){await wait(()=>evaluate(`(()=>{const b=[...document.querySelectorAll('.fleet-panel button')].find(e=>e.textContent.trim()===${JSON.stringify(text)});if(!b||b.disabled)return false;b.click();return true;})()`),'button '+text);}
async function confirm(){await evaluate(`(()=>{const e=[...document.querySelectorAll('.fleet-panel input[type=checkbox]')].find(e=>e.parentElement.textContent.includes('已核对所选车辆'));if(!e.checked)e.click()})()`);}
function check(label,pass){assert.ok(pass,label);console.log('PASS '+label);}
try{
 child=spawn(binary,['--headless=new','--disable-gpu','--no-first-run','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{windowsHide:true,stdio:'ignore'});
 await wait(()=>existsSync(resolve(profile,'DevToolsActivePort')),'browser');
 const port=readFileSync(resolve(profile,'DevToolsActivePort'),'utf8').split('\n')[0];
 const pages=await(await fetch(`http://127.0.0.1:${port}/json/list`)).json();ws=new WebSocket(pages.find(p=>p.type==='page').webSocketDebuggerUrl);
 await new Promise(ok=>ws.addEventListener('open',ok,{once:true}));
 ws.addEventListener('message',e=>{const r=JSON.parse(e.data);if(r.id){const p=rpcPending.get(r.id);if(p){rpcPending.delete(r.id);r.error?p.no(new Error(JSON.stringify(r.error))):p.ok(r.result);}}});
 await rpc('Page.enable');await rpc('Runtime.enable');
 await rpc('Page.addScriptToEvaluateOnNewDocument',{source:`window.WebSocket=class{constructor(){this.timer=setInterval(()=>this.onmessage?.({data:JSON.stringify({source:'backend',connected:true,receivedAt:new Date().toISOString(),tasks:{items:[],totalCount:0},status:{agvs:[],safety:{dispatchingEnabled:true}},devices:{equipment:[]},resources:[],alarms:[]})}),100)}close(){clearInterval(this.timer)}};`});
 await rpc('Page.navigate',{url:`http://127.0.0.1:${server.address().port}/#/engineering-tests`});
 await wait(()=>evaluate(`document.querySelectorAll('.fleet-option-list input').length===10`),'fleet selection loaded');
 await confirm();await click('启动本组测试');
 await wait(()=>evaluate(`document.querySelector('.fleet-panel').textContent.includes('已查询到原申请受理记录')`),'accepted request reconciled');
 check('504 after backend acceptance reconciles by GET and clears the stale failure',starts.length===1&&await evaluate(`!sessionStorage.getItem('fleet-test-start')&&!document.querySelector('.fleet-panel').textContent.includes('isolated start lost reply')`));
 await sleep(2200);check('polling never automatically resends the accepted start',starts.length===1);
 queryOffline=true;await click('查询原记录');
 await wait(()=>evaluate(`document.querySelector('.fleet-panel').textContent.includes('isolated query offline')`),'offline query shown');
 queryOffline=false;await click('查询原记录');
 await wait(()=>evaluate(`!document.querySelector('.fleet-panel').textContent.includes('isolated query offline')`),'query error clears on reconnect');
 check('a successful query clears only the obsolete query failure',await evaluate(`document.querySelector('.fleet-panel').textContent.includes('mock-run-1')`));
 current={...current,stage:'Completed',updatedAt:new Date().toISOString()};acceptStart=false;
 await click('查询原记录');await wait(()=>evaluate(`document.querySelector('.fleet-panel').textContent.includes('本组收尾完成')`),'completed original');
 await confirm();await click('启动本组测试');
 await wait(()=>evaluate(`Boolean(sessionStorage.getItem('fleet-test-start'))&&document.querySelector('.fleet-panel').textContent.includes('isolated start lost reply')`),'unresolved request retained');
 const intent=starts[1];check('missing or different current record retains the exact uncertain intent',starts.length===2&&await evaluate(`JSON.parse(sessionStorage.getItem('fleet-test-start')).requestId===${JSON.stringify(intent.requestId)}`));
 await rpc('Page.reload');await wait(()=>evaluate(`document.querySelector('.fleet-panel')?.textContent.includes('原申请 ${intent.requestId} 尚待核对')`),'pending survives reload');
 check('reload never sends and leaves uncertain selections locked',starts.length===2&&await evaluate(`[...document.querySelectorAll('.fleet-option-list input')].every(e=>e.disabled)`));
 await confirm();await click('重试原申请');await wait(()=>starts.length===3,'explicit original retry');
 check('explicit retry preserves complete original request',JSON.stringify(starts[2])===JSON.stringify(intent));
 assert.deepEqual(errors,[]);console.log('PASS fleet browser checks; mock HTTP only. Evidence: '+profile);
}finally{ws?.close();child?.kill();server.closeAllConnections();await new Promise(ok=>server.close(ok));}
