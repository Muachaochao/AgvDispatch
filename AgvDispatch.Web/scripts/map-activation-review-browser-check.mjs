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
const fixture=resolve(process.env.MAP_ACTIVATION_FIXTURE||'');assert.match(basename(fixture),/^agv-activation-review-[a-f0-9]{32}$/);assert.ok(existsSync(resolve(fixture,'private.db')),'Only the new isolated combination fixture is allowed');
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



 const scenario=process.env.MAP_ACTIVATION_SCENARIO;assert.ok(['committed-crash','request-lost'].includes(scenario));
 let commandNumber=0;async function command(action){const id=String(++commandNumber).padStart(3,'0'),file=resolve(fixture,`command-${id}.json`),reply=resolve(fixture,`reply-${id}.json`);writeFileSync(file+'.tmp',JSON.stringify({action}));renameSync(file+'.tmp',file);await wait(()=>existsSync(reply),'Private command timed out: '+action,45000);const result=JSON.parse(readFileSync(reply,'utf8'));assert.equal(result.error,null,result.error);}
 async function closeDialog(){const p=await evaluate(`(()=>{const b=document.querySelector('[role=dialog] button[aria-label="关闭弹窗"]');const r=b.getBoundingClientRect();return{x:r.x+r.width/2,y:r.y+r.height/2};})()`);await click(p.x,p.y);}
 await rpc('Network.enable');const posts=[];ws.addEventListener('message',event=>{const x=JSON.parse(event.data);if(x.method==='Network.requestWillBeSent'&&x.params.request.method==='POST')posts.push(new URL(x.params.request.url).pathname);});
 await wait(()=>evaluate('document.querySelectorAll(".map-card").length===2&&document.body.textContent.includes("真实后台已连接")'),'Native map catalog missing',25000);
 await button('激活');await evaluate(`(()=>{const c=document.querySelector('input[placeholder="ACTIVATE 组合地图二"]');c.value='ACTIVATE 组合地图二';c.dispatchEvent(new Event('input',{bubbles:true}));const e=document.querySelector('input[placeholder="填写本次现场与地图核对依据"]');e.value='隔离模拟回执丢失验证，无现场设备';e.dispatchEvent(new Event('input',{bubbles:true}));for(const x of document.querySelectorAll('[role=dialog] input[type=checkbox]')){x.checked=true;x.dispatchEvent(new Event('change',{bubbles:true}));}})()`);
 const pauses=[];ws.addEventListener('message',event=>{const x=JSON.parse(event.data);if(x.method==='Fetch.requestPaused')pauses.push(x.params);});await rpc('Fetch.enable',{patterns:[{urlPattern:'*/api/maps/activate',requestStage:scenario==='committed-crash'?'Response':'Request'}]});await button('确认激活');await wait(()=>pauses.length>0,'Original activation was not intercepted',25000);
 const saved=await evaluate('localStorage.getItem("agv-map-activation-intent-v1")'),intent=JSON.parse(saved);check('original release/target intent is durable before the activation can complete',intent.schema===1&&intent.mapId==='COMBO-M2'&&intent.versionId==='COMBO-V2'&&intent.releaseId.startsWith('MAPR-'));
 if(scenario==='committed-crash'){const r=await rpc('Fetch.getResponseBody',{requestId:pauses[0].requestId});const json=JSON.parse(r.base64Encoded?Buffer.from(r.body,'base64').toString('utf8'):r.body);check('lost response really contains a successful native activation',json.accepted===true);}
 await command('inspect-and-interrupt');await rpc('Fetch.failRequest',{requestId:pauses[0].requestId,errorReason:'ConnectionClosed'});await rpc('Fetch.disable');
 await wait(()=>evaluate('Boolean(document.querySelector(".activation-review"))&&document.body.textContent.includes("真实后台未连接 / 数据过期")'),'Lost reply did not preserve an offline review state',25000);
 check('lost reply is displayed as pending review, not proof of failed submission',await evaluate('Boolean(document.querySelector(".activation-pending"))&&localStorage.getItem("agv-map-activation-intent-v1")!==null'));
 check('disconnected proof cannot clear pending state or resume',await evaluate('[...document.querySelectorAll(".activation-review").item(0).closest("[role=dialog]").querySelectorAll("button")].find(b=>b.textContent.includes("已知原结果")).disabled'));
 await shot('01-lost-reply-offline');await rpc('Page.reload');await wait(()=>evaluate('Boolean(document.querySelector(".activation-review"))'),'Reload forgot original pending review',25000);
 check('page reload retains the exact original release intent',await evaluate('localStorage.getItem("agv-map-activation-intent-v1")')===saved);
 await command('restart');await wait(()=>evaluate('document.querySelector(".activation-review")?.textContent.includes("有效观察")'),'Same database reconnect did not refresh original proof',25000);
 check('reconnected review names database/runtime content and held execution gate',await evaluate('document.querySelector(".activation-review").textContent.includes("Held")&&document.querySelector(".activation-review").textContent.includes("本地内容一致")'));
 check('observed outcome distinguishes submitted from original-map state',await evaluate(`document.querySelector('.activation-review').textContent.includes(${JSON.stringify(scenario==='committed-crash'?'已观察提交':'仍观察到原图')})`));
 await shot('02-restarted-proof');
 await button('关闭并保留待核对');await button('刷新');await wait(()=>evaluate('document.querySelectorAll(".map-card").length===2'),'Catalog refresh missing');
 if(scenario==='request-lost')check('original-map observation keeps manual activation disabled',await evaluate('[...document.querySelectorAll(".map-card button")].filter(b=>b.textContent.trim()==="激活").every(b=>b.disabled)'));
 await button('安全切换');await wait(()=>evaluate('document.querySelector("[role=dialog]")?.textContent.includes("MAPR-")'),'Original switch state missing');
 check('pending intent disables direct safety release and new switching',await evaluate('[...document.querySelectorAll("[role=dialog] button")].filter(b=>b.textContent.includes("确认放行")||b.textContent.includes("开始安全切换")).every(b=>b.disabled)'));await closeDialog();
 await button('只读复核原激活');await wait(()=>evaluate('document.querySelector(".activation-review")?.textContent.includes("有效观察")'),'Proof refresh missing');
 if(scenario==='committed-crash'){
  await button('已知原结果，转安全流程核对');await wait(()=>evaluate('document.querySelector("[role=dialog]")?.getAttribute("aria-label")==="地图安全切换"'),'Acknowledgement did not go to safety workflow');
  check('acknowledgement only clears local intent and resets all safety confirmations',await evaluate('localStorage.getItem("agv-map-activation-intent-v1")===null&&[...document.querySelectorAll("[role=dialog] input[type=checkbox]")].every(x=>!x.checked)&&document.querySelector("[role=dialog] textarea").value===""'));await shot('03-safety-not-resumed');
 }else{
  check('unsubmitted observation cannot acknowledge or silently remove pending intent',await evaluate('[...document.querySelectorAll("[role=dialog] button")].find(b=>b.textContent.includes("已知原结果")).disabled&&localStorage.getItem("agv-map-activation-intent-v1")!==null'));await shot('03-original-map-held');
 }
 check('only one browser activation attempt is made across error/reload/reconnect',posts.filter(x=>x==='/api/maps/activate').length===1);
 check('review never calls resume/drain/cancel or retransmits activation',!posts.some(x=>/\/api\/maps\/switch\/(resume|drain|cancel)$/.test(x)));
 check('no browser exception through lost response and restart',errors.length===0);
 writeFileSync(resolve(evidence,'results.json'),JSON.stringify({scenario,passed:checks.length,checks,errors,posts},null,2));console.log(`PASS ${checks.length} activation review browser checks (${scenario}); ${evidence}`);
}catch(error){if(ws?.readyState===1)await shot('failure').catch(()=>{});console.error(error);process.exitCode=1;}
finally{if(ws?.readyState===1)await rpc('Browser.close').catch(()=>{});ws?.close();child.kill();}
