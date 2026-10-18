// Local browser integration check. Uses a separate disposable profile and synthetic data only.
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
const target=process.env.EDITOR_TEST_URL;assert.ok(target,'Set isolated EDITOR_TEST_URL');
assert.ok(['127.0.0.1','localhost'].includes(new URL(target).hostname),'Only loopback test servers are allowed');
const binary=process.env.EDITOR_TEST_BROWSER||['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe','C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert.ok(binary,'Set EDITOR_TEST_BROWSER to an installed Chromium browser');
const profile=resolve(root,'.codex-tmp',`blocking-browser-${Date.now()}`),evidence=resolve(root,'AgvDispatch.Web/evidence/blocking-interactions');mkdirSync(profile,{recursive:true});mkdirSync(evidence,{recursive:true});
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

 await rpc('Network.enable');const posts=[];let inDemo=false;ws.addEventListener('message',event=>{const x=JSON.parse(event.data);if(x.method==='Network.requestWillBeSent'&&x.params.request.method==='POST')posts.push({url:x.params.request.url,demo:inDemo});});
 await wait(()=>evaluate('document.querySelectorAll(".waiting-table tbody tr").length===25&&document.querySelector(".waiting-summary").textContent.includes("有效观察")'),'Original waiting page unavailable');
 check('original persisted waiting records remain visible',await evaluate('document.querySelector(".waiting-summary").textContent.includes("等待总数 29")'));
 await button('阻塞链');
 await wait(()=>evaluate('document.querySelector(".blocking-detail")?.textContent.includes("有效观察")'),'Native blocking detail unavailable');
 check('dialog names original backend source and release requirements',await evaluate('document.querySelector(".blocking-detail").textContent.includes("原后台只读快照")&&document.querySelector(".blocking-detail").textContent.includes("不是放行许可")'));
 check('native multi-step chain shows actual dependency loop and same-task edges',await evaluate('document.querySelector(".blocking-detail").textContent.includes("观察到循环依赖")&&document.querySelector(".blocking-links").textContent.includes("同一任务另一等待步骤")'));
 check('owner task and original occupancy are visible without invented location',await evaluate('document.querySelector(".blocking-links").textContent.includes("BLOCK-TASK")&&document.querySelector(".blocking-links").textContent.includes("BLOCKER")'));
 check('release requirements remain unassessed and never authorize unlocking',await evaluate('document.querySelector(".blocking-detail").textContent.includes("未评估")&&![...document.querySelectorAll(".blocking-detail button")].some(b=>/^(解锁|放行|释放)$/.test(b.textContent.trim()))'));
 check('recent history includes original manual audit and excludes unrelated resource events',await evaluate('document.querySelector(".blocking-events").textContent.includes("ManualReleaseAudit")&&!document.querySelector(".blocking-events").textContent.includes("Unrelated")'));
 await shot('native-blocking-chain');
 await button('刷新阻塞链');await wait(()=>evaluate('document.querySelector(".blocking-detail")?.textContent.includes("有效观察")'),'Refresh failed');check('readonly refresh preserves a valid original chain',true);
 await button('定位阻塞 NARROW');await wait(()=>evaluate('!document.querySelector(".blocking-detail")&&document.querySelector(".resource-location")?.textContent.includes("已定位资源 NARROW")'),'Resource navigation lost while closing dialog');check('resource link closes modal and locates original map boundary',true);await shot('blocking-resource-located');
 await button('阻塞链');await wait(()=>evaluate('document.querySelector(".blocking-detail")?.textContent.includes("有效观察")'),'Reopen failed');await button('查看任务 BLOCK-TASK');await wait(()=>evaluate('!document.querySelector(".blocking-detail")&&Boolean(document.querySelector("[role=dialog]"))'),'Task navigation lost while closing dialog');check('owner task navigation replaces the blocking dialog',await evaluate('document.querySelector("[role=dialog]").textContent.includes("BLOCK-TASK")'));
 await button('关闭');await wait(()=>evaluate('!document.querySelector("[role=dialog]")'),'Task did not close');
 await button('演示模式');inDemo=true;await wait(()=>evaluate('Boolean(document.querySelector(".traffic-rehearsal"))'),'Demo missing');await button('开始 / 重置演练');
 await wait(()=>evaluate('document.querySelector(".waiting-summary")?.textContent.includes("有效观察")&&[...document.querySelectorAll(".waiting-table button")].some(b=>b.textContent.trim()==="阻塞链"&&!b.disabled)'),'Demo waiting unavailable');
 await button('阻塞链');await wait(()=>evaluate('document.querySelector(".blocking-detail")?.textContent.includes("有效观察")'),'Demo chain unavailable');
 check('demo clearly names synthetic source and limited chain',await evaluate('document.querySelector(".blocking-detail").textContent.includes("浏览器合成示例")&&document.querySelector(".blocking-detail").textContent.includes("未评估")'));
 await shot('demo-blocking-chain');await button('关闭');await button('退出演示');inDemo=false;
 check('demo detail sends no backend query or control POST',posts.filter(x=>x.demo).length===0);
 check('native detail only uses readonly traffic query POST',posts.filter(x=>!x.demo).every(x=>/\/api\/traffic\/(waiting|blocking)$/.test(new URL(x.url).pathname)));
 check('no uncaught browser exceptions',errors.length===0);
 writeFileSync(resolve(evidence,'results.json'),JSON.stringify({passed:checks.length,checks,errors},null,2));console.log(`PASS ${checks.length} blocking browser checks; ${evidence}`);
}catch(error){if(ws?.readyState===1)await shot('failure').catch(()=>{});console.error(error);process.exitCode=1;}
finally{if(ws?.readyState===1)await rpc('Browser.close').catch(()=>{});ws?.close();child.kill();}
