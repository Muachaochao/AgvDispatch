// Loopback mock HTTP only: never uses the field backend, PLC or vehicle.
import {createServer} from 'node:http';
import {spawn} from 'node:child_process';
import {existsSync,mkdirSync,readFileSync} from 'node:fs';
import {resolve,dirname,sep} from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';
const project=resolve(dirname(fileURLToPath(import.meta.url)),'../..'),dist=resolve(project,'AgvDispatch.Web/dist');
const profile=resolve(project,'.codex-tmp',`plc-cycle-browser-${Date.now()}`);
mkdirSync(profile,{recursive:true});
const binary=['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe','C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert.ok(binary,'Chromium browser required');
let current=null,offline=false,delay=0,inflight=0,maxInflight=0,seq=0,ws,child,resets=0;
const arms=[],pending=new Map(),errors=[];
const sleep=ms=>new Promise(ok=>setTimeout(ok,ms));
const status=()=>({enabled:true,canArm:!current||['Disarmed','Completed'].includes(current.stage),blockReason:null,vehicleId:'AGV-01',stationId:'CNC-001',pickupSite:'LM2',waitingSite:'LM5',dumpingSite:'LM6',message:'mock',current});
const server=createServer(async(req,res)=>{
 try{
  const url=new URL(req.url,'http://127.0.0.1');
  const json=(code,data)=>{res.writeHead(code,{'Content-Type':'application/json'});res.end(JSON.stringify(data));};
  if(url.pathname==='/api/plc-cycle-test'){
   inflight++;maxInflight=Math.max(maxInflight,inflight);
   try{await sleep(delay);return offline?json(503,{detail:'offline fixture'}):json(200,status());}finally{inflight--;}
  }
  if(url.pathname.startsWith('/api/plc-cycle-test/')){
   let bytes='';for await(const part of req)bytes+=part;const body=JSON.parse(bytes);
   if(url.pathname.endsWith('/arm')){
    arms.push(body);assert.equal(body.siteClearConfirmed,true);assert.equal(body.emptyVehicleConfirmed,true);
    if(arms.length===1)return json(503,{detail:'fixture lost reply; query original'});
    current={...body,cycleId:'mock-cycle',vehicleId:'AGV-01',stationId:'CNC-001',stage:'AwaitDumperReady',message:'mock active',robotMap:'mock',robotMapMd5:'a'.repeat(32),updatedAt:new Date().toISOString(),children:[],events:[]};
    return json(200,status());
   }
   if(url.pathname.endsWith('/reset')){
    assert.equal(body.cycleId,current.cycleId);assert.ok(body.reason.trim().length>=10);resets++;current={...current,stage:'Disarmed'};return json(200,status());
   }
   throw new Error('unexpected mutation '+url.pathname);
  }
  if(url.pathname.startsWith('/api/'))return json(503,{error:'mock only'});
  const file=resolve(dist,'.'+(url.pathname==='/'?'/index.html':url.pathname));
  if(!file.startsWith(dist+sep)||!existsSync(file)){res.writeHead(404);return res.end();}
  res.writeHead(200,{'Content-Type':file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':'text/html'});res.end(readFileSync(file));
 }catch(e){errors.push(String(e));res.writeHead(500);res.end('{}');}
});
await new Promise(ok=>server.listen(0,'127.0.0.1',ok));
async function wait(fn,label){const end=Date.now()+20000;while(Date.now()<end){if(await fn())return;await sleep(100);}throw new Error(label);}
function rpc(method,params={}){return new Promise((ok,no)=>{const id=++seq,t=setTimeout(()=>no(new Error(method+' timeout')),15000);pending.set(id,{ok:v=>{clearTimeout(t);ok(v);},no});ws.send(JSON.stringify({id,method,params}));});}
async function evaluate(expression){const r=await rpc('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(r.exceptionDetails.text);return r.result.value;}
async function click(label){await wait(()=>evaluate(`(()=>{const b=[...document.querySelectorAll('.cycle-page button')].find(e=>e.textContent.trim()===${JSON.stringify(label)});if(!b||b.disabled)return false;b.click();return true;})()`),'Button unavailable '+label);}
async function confirm(){await evaluate(`document.querySelectorAll('.arm-card input').forEach(e=>{if(!e.checked)e.click()})`);}
const check=(label,value)=>{assert.ok(value,label);console.log('PASS '+label);};
try{
 child=spawn(binary,['--headless=new','--disable-gpu','--no-first-run','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{windowsHide:true,stdio:'ignore'});
 await wait(()=>existsSync(resolve(profile,'DevToolsActivePort')),'Browser start');
 const port=readFileSync(resolve(profile,'DevToolsActivePort'),'utf8').split('\n')[0];
 const pages=await(await fetch(`http://127.0.0.1:${port}/json/list`)).json();
 ws=new WebSocket(pages.find(p=>p.type==='page').webSocketDebuggerUrl);
 await new Promise(ok=>ws.addEventListener('open',ok,{once:true}));
 ws.addEventListener('message',event=>{const r=JSON.parse(event.data);if(r.id){const p=pending.get(r.id);if(p){pending.delete(r.id);r.error?p.no(new Error(JSON.stringify(r.error))):p.ok(r.result);}}else if(r.method==='Page.javascriptDialogOpening')void rpc('Page.handleJavaScriptDialog',{accept:true,promptText:'已现场核对车辆下降停稳且三个输入均清零'});});
 await rpc('Page.enable');await rpc('Runtime.enable');
 const url=`http://127.0.0.1:${server.address().port}/#/plc-cycle-test`;
 await rpc('Page.navigate',{url});
 await wait(()=>evaluate(`document.querySelectorAll('.arm-card input').length===3`),'Page loaded');
 check('Cannot arm without all three confirmations',await evaluate(`document.querySelector('.arm-card button').disabled`));
 await confirm();await click('武装本轮并等待PLC呼叫');
 await wait(()=>evaluate(`document.querySelector('.error')?.textContent.includes('查询原任务')`),'Lost reply shown');
 const saved=await evaluate(`JSON.parse(sessionStorage.getItem('plc-cycle-arm-request')).requestId`);
 check('Lost response retains exact request identity',arms.length===1&&saved===arms[0].requestId);
 await rpc('Page.reload');await wait(()=>evaluate(`document.querySelectorAll('.arm-card input').length===3`),'Reloaded');
 await confirm();await click('武装本轮并等待PLC呼叫');
 await wait(()=>evaluate(`document.querySelector('.state')?.textContent==='AwaitDumperReady'`),'Arm result');
 check('Reload and retry reuse the original request ID',arms.length===2&&arms[0].requestId===arms[1].requestId);
 check('Current phase explains the exact PLC Ready address',await evaluate(`document.querySelector('.status-card').textContent.includes('DB1.DBX610.2')`));
 delay=2200;maxInflight=0;await sleep(6500);
 check('Polling never overlaps requests',maxInflight===1);
 delay=0;await sleep(2500);current={...current,stage:'Hold'};
 await click('现场核对后复位本轮');
 await wait(()=>resets===1,'Reset request');check('Reset includes original cycle and explicit reason',resets===1);
 offline=true;await wait(()=>evaluate(`document.querySelector('.error')?.textContent.includes('offline fixture')`),'Offline');
 await confirm();check('Offline state disables arming despite old ready status',await evaluate(`document.querySelector('.arm-card button').disabled`));
 assert.deepEqual(errors,[]);console.log('PASS browser checks; mock HTTP only');
}finally{ws?.close();child?.kill();server.closeAllConnections();await new Promise(ok=>server.close(ok));}
