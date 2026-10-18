// Local browser integration check. Uses a separate disposable profile and synthetic data only.
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, renameSync } from 'node:fs';
import { resolve, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
const target=process.env.EDITOR_TEST_URL;assert.ok(target,'Set isolated EDITOR_TEST_URL');
assert.ok(['127.0.0.1','localhost'].includes(new URL(target).hostname),'Only loopback test servers are allowed');
const binary=process.env.EDITOR_TEST_BROWSER||['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe','C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert.ok(binary,'Set EDITOR_TEST_BROWSER to an installed Chromium browser');
const fixture=resolve(process.env.TRAFFIC_COMBINATION_FIXTURE||'');assert.match(basename(fixture),/^agv-traffic-combination-[a-f0-9]{32}$/);assert.ok(existsSync(resolve(fixture,'private.db')),'Only the new isolated combination fixture is allowed');
const profile=resolve(fixture,'browser-profile'),evidence=resolve(fixture,'browser-screenshots');mkdirSync(profile,{recursive:true});mkdirSync(evidence,{recursive:true});
const child=spawn(binary,['--headless=new','--enable-unsafe-swiftshader','--use-angle=swiftshader','--no-first-run','--no-default-browser-check','--remote-debugging-address=127.0.0.1','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{windowsHide:true,stdio:'ignore'});
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
let ws,seq=0;const pending=new Map(),errors=[],checks=[];
async function wait(fn,message,timeout=15000){const until=Date.now()+timeout;while(Date.now()<until){if(await fn())return;await sleep(80);}throw new Error(message);}
function rpc(method,params={}){return new Promise((ok,no)=>{const id=++seq,timer=setTimeout(()=>{pending.delete(id);no(new Error(`Timeout ${method}`));},15000);pending.set(id,{ok:v=>{clearTimeout(timer);ok(v);},no:e=>{clearTimeout(timer);no(e);}});ws.send(JSON.stringify({id,method,params}));});}
async function evaluate(expression){const r=await rpc('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(r.exceptionDetails.text+' '+r.exceptionDetails.exception?.description);return r.result.value;}
async function settle(){await evaluate('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');}
async function click(x,y,modifiers=0){await rpc('Input.dispatchMouseEvent',{type:'mouseMoved',x,y});await rpc('Input.dispatchMouseEvent',{type:'mousePressed',x,y,button:'left',clickCount:1,modifiers});await rpc('Input.dispatchMouseEvent',{type:'mouseReleased',x,y,button:'left',clickCount:1,modifiers});await settle();}
async function button(text){const point=await evaluate(`(()=>{const b=[...document.querySelectorAll('button')].find(b=>b.textContent.trim()===${JSON.stringify(text)}&&b.getClientRects().length);if(!b)throw new Error('Button missing: '+${JSON.stringify(text)});if(b.disabled)throw new Error('Button disabled: '+${JSON.stringify(text)});b.scrollIntoView({block:'center'});const r=b.getBoundingClientRect();return{x:r.x+r.width/2,y:r.y+r.height/2};})()`);await click(point.x,point.y);}
function check(name,fn){assert.ok(fn,name);checks.push(name);console.log('PASS '+name);}
async function shot(name){const r=await rpc('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});writeFileSync(resolve(evidence,name+'.png'),Buffer.from(r.data,'base64'));}

try{
 await wait(()=>existsSync(resolve(profile,'DevToolsActivePort')),'Browser did not expose its isolated debug port');
 const port=readFileSync(resolve(profile,'DevToolsActivePort'),'utf8').split('\n')[0];
 const pages=await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();const page=pages.find(p=>p.type==='page');assert.ok(page);
 ws=new WebSocket(page.webSocketDebuggerUrl);await new Promise((ok,no)=>{ws.addEventListener('open',ok,{once:true});ws.addEventListener('error',no,{once:true});});
 ws.addEventListener('message',event=>{const r=JSON.parse(event.data);if(r.id){const p=pending.get(r.id);if(p){pending.delete(r.id);r.error?p.no(new Error(JSON.stringify(r.error))):p.ok(r.result);}}else if(r.method==='Runtime.exceptionThrown')errors.push(r.params.exceptionDetails.exception?.description||r.params.exceptionDetails.text);});
 await rpc('Page.enable');await rpc('Runtime.enable');await rpc('Emulation.setDeviceMetricsOverride',{width:1680,height:1100,deviceScaleFactor:1,mobile:false});await rpc('Page.navigate',{url:target});


 let commandNumber=0;async function command(action){const id=String(++commandNumber).padStart(3,'0'),file=resolve(fixture,`command-${id}.json`),reply=resolve(fixture,`reply-${id}.json`);writeFileSync(file+'.tmp',JSON.stringify({action}));renameSync(file+'.tmp',file);await wait(()=>existsSync(reply),`Fixture command timed out: ${action}`,45000);const result=JSON.parse(readFileSync(reply,'utf8'));assert.equal(result.error,null,result.error);}
 async function openChain(task){await wait(()=>evaluate(`document.querySelector('.waiting-summary')?.textContent.includes('有效观察')&&[...document.querySelectorAll('.waiting-table tbody tr')].some(r=>r.textContent.includes(${JSON.stringify(task)}))`),'Expected original waiting record missing',25000);const point=await evaluate(`(()=>{const row=[...document.querySelectorAll('.waiting-table tbody tr')].find(r=>r.textContent.includes(${JSON.stringify(task)}));const b=[...row.querySelectorAll('button')].find(b=>b.textContent.trim()==='阻塞链');if(b.disabled)throw new Error('Old waiting observation');b.scrollIntoView({block:'center'});const r=b.getBoundingClientRect();return{x:r.x+r.width/2,y:r.y+r.height/2};})()`);await click(point.x,point.y);await wait(()=>evaluate('document.querySelector(".blocking-detail")?.textContent.includes("有效观察")'),'Fresh blocking detail missing',25000);}
 await rpc('Network.enable');const posts=[];ws.addEventListener('message',event=>{const x=JSON.parse(event.data);if(x.method==='Network.requestWillBeSent'&&x.params.request.method==='POST')posts.push(x.params.request.url);});
 await openChain('COMBO-C');
 check('initial C chain shows actual A/B dependency loop',await evaluate('document.querySelector(".blocking-links").textContent.includes("COMBO-A")&&document.querySelector(".blocking-links").textContent.includes("COMBO-B")&&document.querySelector(".blocking-detail").textContent.includes("观察到循环依赖")'));
 await shot('01-initial-cycle');
 await command('advance');await wait(()=>evaluate('document.querySelector(".blocking-links")?.textContent.includes("COMBO-B")&&!document.querySelector(".blocking-links")?.textContent.includes("COMBO-A")'),'Detail kept an old blocker after normal owner handover',20000);
 check('same open dialog refreshes from A to B and removes old cycle',await evaluate('document.querySelector(".blocking-detail").textContent.includes("当前展开范围未观察到循环")'));
 await button('定位阻塞 R1');await wait(()=>evaluate('!document.querySelector(".blocking-detail")&&document.querySelector(".resource-location")?.textContent.includes("已定位资源 R1")'),'Original resource location unavailable');
 check('old map resource is located before connection loss',true);
 await openChain('COMBO-C');await command('fault');await wait(()=>evaluate('document.querySelector(".blocking-detail")?.textContent.includes("需现场核对")&&document.querySelector(".blocking-links")?.textContent.includes("心跳过期")'),'Retained cancellation observation not refreshed',20000);
 check('cancellation pending and expired heartbeat remain a visible blocker',true);await shot('02-retained-cancellation');
 await command('disconnect');await wait(()=>evaluate('!document.querySelector(".blocking-detail")&&document.querySelectorAll(".waiting-table tbody tr").length===0&&document.body.textContent.includes("真实后台未连接 / 数据过期")'),'Disconnected backend left usable old records',25000);
 check('real Service outage closes old chain and clears waiting rows',true);
 check('outage clears previous resource focus without synthetic fallback',await evaluate('!document.querySelector(".resource-location")?.textContent.includes("已定位资源")&&!document.querySelector(".traffic-rehearsal")'));
 await shot('03-service-disconnected');
 await command('reconnect');await wait(()=>evaluate('document.querySelector(".waiting-summary")?.textContent.includes("有效观察")&&[...document.querySelectorAll(".waiting-table tbody tr")].some(r=>r.textContent.includes("COMBO-C")&&r.textContent.includes("CAR-B"))'),'Same database reconnect did not refresh waiting rows',25000);
 check('reconnect reads original persisted blocker without reopening old dialog',await evaluate('!document.querySelector(".blocking-detail")'));
 await openChain('COMBO-C');check('reopened chain still requires review of original B occupancy',await evaluate('document.querySelector(".blocking-detail").textContent.includes("需现场核对")&&document.querySelector(".blocking-links").textContent.includes("COMBO-B")&&!document.querySelector(".blocking-links").textContent.includes("COMBO-A")'));
 await shot('04-reconnected-retained');
 const paused=[];ws.addEventListener('message',event=>{const x=JSON.parse(event.data);if(x.method==='Fetch.requestPaused')paused.push(x.params);});
 await rpc('Fetch.enable',{patterns:[{urlPattern:'*/api/traffic/blocking',requestStage:'Response'}]});await button('刷新阻塞链');await wait(()=>paused.length>0,'Old-map response was not paused');const old=paused[0];const body=await rpc('Fetch.getResponseBody',{requestId:old.requestId});const original=JSON.parse(body.base64Encoded?Buffer.from(body.body,'base64').toString('utf8'):body.body);
 check('delayed response really belongs to original M1/C context',original.mapId==='COMBO-M1'&&original.detail.taskId==='COMBO-C');
 await command('switch-map');await wait(()=>evaluate('document.body.textContent.includes("COMBO-M2")&&!document.querySelector(".blocking-detail")'),'Successful native map switch did not clear old chain',25000);
 check('map change closes the open old-map dialog',true);
 await rpc('Fetch.fulfillRequest',{requestId:old.requestId,responseCode:old.responseStatusCode,body:body.base64Encoded?body.body:Buffer.from(body.body).toString('base64'),responseHeaders:old.responseHeaders});await rpc('Fetch.disable');await sleep(600);
 check('late old-map response cannot resurrect a closed chain or resource focus',await evaluate('!document.querySelector(".blocking-detail")&&!document.querySelector(".resource-location")?.textContent.includes("已定位资源")'));
 await openChain('COMBO-F');check('new map same resource ID links new E/F ownership only',await evaluate('document.querySelector(".blocking-links").textContent.includes("COMBO-E")&&document.querySelector(".blocking-links").textContent.includes("COMBO-M2")&&!document.querySelector(".blocking-links").textContent.includes("COMBO-B")'));
 await shot('05-new-map-chain');await button('定位阻塞 R1');await wait(()=>evaluate('!document.querySelector(".blocking-detail")&&document.querySelector(".resource-location")?.textContent.includes("已定位资源 R1")'),'New map resource location missing');
 check('new map R1 can be located with the new boundary',await evaluate('document.body.textContent.includes("COMBO-M2")'));await shot('06-new-map-resource');
 check('browser combination uses only readonly waiting/blocking POSTs',posts.every(x=>/\/api\/traffic\/(waiting|blocking)$/.test(new URL(x).pathname)));
 check('browser never replaces backend observations with demo data',await evaluate('!document.querySelector(".traffic-rehearsal")'));
 check('no uncaught browser exceptions during outage and map changes',errors.length===0);
 writeFileSync(resolve(evidence,'results.json'),JSON.stringify({passed:checks.length,checks,errors},null,2));console.log(`PASS ${checks.length} combination browser checks; ${evidence}`);
}catch(error){if(ws?.readyState===1)await shot('failure').catch(()=>{});console.error(error);process.exitCode=1;}
finally{if(ws?.readyState===1)await rpc('Browser.close').catch(()=>{});ws?.close();child.kill();}
