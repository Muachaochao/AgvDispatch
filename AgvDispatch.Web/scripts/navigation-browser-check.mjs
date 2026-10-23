// Synthetic, loopback-only UI test. Never opens the field service or contacts a robot.
import {createServer} from 'node:http';
import {spawn} from 'node:child_process';
import {existsSync,mkdirSync,readFileSync,writeFileSync} from 'node:fs';
import {resolve,dirname,sep} from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';

const project=resolve(dirname(fileURLToPath(import.meta.url)),'../..'),dist=resolve(project,'AgvDispatch.Web/dist');
const evidence=resolve(project,'artifacts/navigation-browser'),profile=resolve(project,'.codex-tmp',`navigation-browser-${Date.now()}`);
mkdirSync(evidence,{recursive:true});mkdirSync(profile,{recursive:true});
const binary=process.env.EDITOR_TEST_BROWSER||['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe','C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert.ok(binary,'A Chromium browser is required');
let ready=false,current=null,submits=0,previews=0,offline=false,seq=0,ws,child;
const pending=new Map(),errors=[],checks=[];
const md5='a'.repeat(32),now=()=>new Date().toISOString();
const catalog={vehicleId:'SIM',robotMap:'fixture',robotMapMd5:md5,stations:[{id:'A',x:0,y:0},{id:'B',x:1,y:0},{id:'C',x:2,y:0}],suggestedSourceSite:'A',capturedAt:now()};
const server=createServer(async(req,res)=>{
 try{
  const url=new URL(req.url,'http://127.0.0.1');
  function json(code,data){res.writeHead(code,{'Content-Type':'application/json'});res.end(JSON.stringify(data));}
  if(url.pathname==='/api/navigation-test')return offline?json(503,{detail:'模拟后台断连'}):json(200,{enabled:true,vehicleId:'SIM',ready,blockReason:ready?null:'启动恢复核对尚未放行',current,message:'synthetic'});
  if(url.pathname==='/api/navigation-test/stations')return json(200,{...catalog,capturedAt:now()});
  if(url.pathname.startsWith('/api/navigation-test/')){
   let bytes='';for await(const part of req)bytes+=part;
   const body=JSON.parse(bytes);
   if(url.pathname.endsWith('/preview')){
    assert.equal(body.vehicleId,'SIM');assert.equal(body.expectedRobotMap,'fixture');assert.equal(body.expectedRobotMapMd5,md5);
    assert.equal(body.sourceSite,'A');assert.equal(body.targetSite,'C');previews++;
    return json(200,{previewId:`preview-${previews}`,expiresAt:new Date(Date.now()+30000).toISOString(),vehicleId:'SIM',sourceSite:'A',targetSite:'C',robotMap:'fixture',robotMapMd5:md5,route:['A','B','C']});
   }
   if(url.pathname.endsWith('/submit')){
    assert.equal(body.siteClearConfirmed,true);assert.equal(body.previewId,`preview-${previews}`);submits++;
    current={taskId:'test',vendorTaskId:'unique-test',vehicleId:'SIM',sourceSite:'A',targetSite:'C',robotMap:'fixture',robotMapMd5:md5,route:['A','B','C'],navigationApi:3051,state:'Running',message:'原任务运行中',updatedAt:now(),observedAt:now(),currentStation:'B',x:1,y:0};
    return json(200,current);
   }
   return json(404,{detail:'Unexpected navigation endpoint'});
  }
  if(url.pathname.startsWith('/api/'))return json(503,{error:'Synthetic UI test, no field service connection'});
  const file=resolve(dist,'.'+(url.pathname==='/'?'/index.html':url.pathname));
  if(!file.startsWith(dist+sep)||!existsSync(file)){res.writeHead(404);return res.end();}
  res.writeHead(200,{'Content-Type':file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':'text/html'});res.end(readFileSync(file));
 }catch(e){errors.push(String(e));res.writeHead(500);res.end('{}');}
});
await new Promise(ok=>server.listen(0,'127.0.0.1',ok));
const port=server.address().port,sleep=ms=>new Promise(ok=>setTimeout(ok,ms));
async function wait(fn,label,timeout=15000){const end=Date.now()+timeout;while(Date.now()<end){if(await fn())return;await sleep(80);}throw new Error(label);}
function rpc(method,params={}){return new Promise((ok,no)=>{const id=++seq,timer=setTimeout(()=>{pending.delete(id);no(new Error(method+' timed out'));},15000);pending.set(id,{ok:v=>{clearTimeout(timer);ok(v);},no:e=>{clearTimeout(timer);no(e);}});ws.send(JSON.stringify({id,method,params}));});}
async function evaluate(expression){const r=await rpc('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(r.exceptionDetails.exception?.description||r.exceptionDetails.text);return r.result.value;}
async function button(label){await wait(()=>evaluate(`(()=>{const b=[...document.querySelectorAll('.nav-test button')].find(e=>e.textContent.trim()===${JSON.stringify(label)});if(!b||b.disabled)return false;b.click();return true;})()`),'Button unavailable: '+label);}
async function select(index,value){await evaluate(`(()=>{const s=document.querySelectorAll('.nav-test select')[${index}];s.value=${JSON.stringify(value)};s.dispatchEvent(new Event('change',{bubbles:true}));})()`);await sleep(100);}
function check(label,passed){assert.ok(passed,label);checks.push(label);console.log('PASS '+label);}
async function screenshot(name){const shot=await rpc('Page.captureScreenshot',{format:'png',captureBeyondViewport:true});writeFileSync(resolve(evidence,name+'.png'),Buffer.from(shot.data,'base64'));}
try{
 child=spawn(binary,['--headless=new','--disable-gpu','--no-first-run','--no-default-browser-check','--remote-debugging-address=127.0.0.1','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{windowsHide:true,stdio:'ignore'});
 await wait(()=>existsSync(resolve(profile,'DevToolsActivePort')),'Browser did not start');
 const browserPort=readFileSync(resolve(profile,'DevToolsActivePort'),'utf8').split('\n')[0];
 const pages=await(await fetch(`http://127.0.0.1:${browserPort}/json/list`)).json(),page=pages.find(p=>p.type==='page');assert.ok(page);
 ws=new WebSocket(page.webSocketDebuggerUrl);await new Promise((ok,no)=>{ws.addEventListener('open',ok,{once:true});ws.addEventListener('error',no,{once:true});});
 ws.addEventListener('message',event=>{const r=JSON.parse(event.data);if(r.id){const p=pending.get(r.id);if(p){pending.delete(r.id);r.error?p.no(new Error(JSON.stringify(r.error))):p.ok(r.result);}}else if(r.method==='Runtime.exceptionThrown')errors.push(r.params.exceptionDetails.exception?.description||r.params.exceptionDetails.text);});
 await rpc('Page.enable');await rpc('Runtime.enable');await rpc('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await rpc('Page.navigate',{url:`http://127.0.0.1:${port}/#/navigation-test`});
 await wait(()=>evaluate(`document.querySelectorAll('.nav-test select').length===2`),'Catalog did not load');
 check('Station choices come from robot catalog and current source is suggested',await evaluate(`document.querySelectorAll('.nav-test select')[0].value==='A'&&document.querySelectorAll('.nav-test select')[1].options.length===4`));
 check('Recovery blocker is visible and no motion was requested',await evaluate(`document.querySelector('.nav-test').textContent.includes('启动恢复核对尚未放行')`)&&submits===0);
 await select(1,'C');
 check('Closed recovery gate disables route preview',await evaluate(`[...document.querySelectorAll('.nav-test button')].find(e=>e.textContent.includes('核验车端')).disabled`));
 await screenshot('blocked');
 ready=true;await button('查询原任务');await button('核验车端地图、起点和路线');
 await wait(()=>evaluate(`document.querySelector('.preview .route')?.textContent==='A → B → C'`),'Multi-hop preview missing');
 check('Complete multi-hop itinerary is shown before sending',true);
 check('Explicit confirmation is required',await evaluate(`document.querySelector('.send').disabled`));
 await evaluate(`document.querySelector('.preview input[type=checkbox]').click()`);
 await select(1,'B');check('Changing station clears previous preview and confirmation',await evaluate(`!document.querySelector('.preview')`)&&submits===0);
 await select(1,'C');await button('核验车端地图、起点和路线');await wait(()=>evaluate(`Boolean(document.querySelector('.preview input[type=checkbox]'))`),'Confirmation missing');
 await screenshot('route-preview');
 await evaluate(`document.querySelector('.preview input[type=checkbox]').click()`);await button('发送单次导航');
 await wait(()=>evaluate(`document.querySelector('.nav-test').textContent.includes('原任务运行中')`),'Task status missing');
 check('One navigation request and active task disables station changes',submits===1&&await evaluate(`[...document.querySelectorAll('.nav-test select')].every(s=>s.disabled)&&!document.querySelector('.send')`));
 check('Physical observation and original task id are visible',await evaluate(`document.querySelector('.nav-test').textContent.includes('unique-test')&&document.querySelector('.nav-test').textContent.includes('当前站点：B')`));
 current={...current,state:'Completed',currentStation:'C',x:2,message:'原任务已完成，车端地图一致，车辆已在所选终点静止。',updatedAt:now(),observedAt:now()};
 catalog.suggestedSourceSite='C';await button('查询原任务');
 await wait(()=>evaluate(`document.querySelector('.nav-test').textContent.includes('已完成并核对到点')`),'Completion missing');
 await button('重新读取车端站点');await wait(()=>evaluate(`document.querySelector('.nav-test select')?.value==='C'`),'Current destination was not suggested as next source');
 check('Completion unlocks station selection without automatic resubmission',submits===1&&await evaluate(`[...document.querySelectorAll('.nav-test select')].every(s=>!s.disabled)`));
 await screenshot('completed');
 await rpc('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 check('Navigation content stays within narrow viewport',await evaluate(`document.querySelector('.nav-test').scrollWidth<=document.querySelector('.nav-test').clientWidth+1`));
 await screenshot('mobile');
 current=null;offline=true;await rpc('Page.reload');await wait(()=>evaluate(`document.querySelector('.nav-test')?.textContent.includes('尚未取得可靠的后台任务状态')`),'Offline task state was not explicit');
 check('Failed status query is never presented as no task sent',await evaluate(`!document.querySelector('.nav-test').textContent.includes('后台当前没有已记录的测试任务')`)&&submits===1);
 check('No uncaught browser or synthetic server exceptions',errors.length===0);
 writeFileSync(resolve(evidence,'results.json'),JSON.stringify({passed:checks.length,checks,submits,previews,errors},null,2));
 console.log(`PASS ${checks.length} browser checks; synthetic endpoints only`);
}catch(e){if(ws?.readyState===1)await screenshot('failure').catch(()=>{});writeFileSync(resolve(evidence,'failure.txt'),String(e.stack||e));console.error(e);process.exitCode=1;}
finally{if(ws?.readyState===1)await rpc('Browser.close').catch(()=>{});ws?.close();child?.kill();await new Promise(ok=>server.close(ok));}
