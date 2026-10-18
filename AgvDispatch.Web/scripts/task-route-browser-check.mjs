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
const profile=resolve(root,'.codex-tmp',`editor-browser-${Date.now()}`),evidence=resolve(root,'AgvDispatch.Web/evidence/task-route-interactions');mkdirSync(profile,{recursive:true});mkdirSync(evidence,{recursive:true});
const child=spawn(binary,['--headless=new','--disable-gpu','--no-first-run','--no-default-browser-check','--remote-debugging-address=127.0.0.1','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{windowsHide:true,stdio:'ignore'});
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
let ws,seq=0;const pending=new Map(),errors=[],checks=[];
async function wait(fn,message,timeout=15000){const until=Date.now()+timeout;while(Date.now()<until){if(await fn())return;await sleep(80);}throw new Error(message);}
function rpc(method,params={}){return new Promise((ok,no)=>{const id=++seq,timer=setTimeout(()=>{pending.delete(id);no(new Error(`Timeout ${method}`));},15000);pending.set(id,{ok:v=>{clearTimeout(timer);ok(v);},no:e=>{clearTimeout(timer);no(e);}});ws.send(JSON.stringify({id,method,params}));});}
async function evaluate(expression){const r=await rpc('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(r.exceptionDetails.text+' '+r.exceptionDetails.exception?.description);return r.result.value;}
async function settle(){await evaluate('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');}
async function click(x,y,modifiers=0){await rpc('Input.dispatchMouseEvent',{type:'mouseMoved',x,y});await rpc('Input.dispatchMouseEvent',{type:'mousePressed',x,y,button:'left',clickCount:1,modifiers});await rpc('Input.dispatchMouseEvent',{type:'mouseReleased',x,y,button:'left',clickCount:1,modifiers});await settle();}
async function button(text){const point=await evaluate(`(()=>{const b=[...document.querySelectorAll('button')].find(b=>b.textContent.trim()===${JSON.stringify(text)}&&b.getClientRects().length);if(!b)throw new Error('Button missing: '+${JSON.stringify(text)});if(b.disabled)throw new Error('Button disabled: '+${JSON.stringify(text)});b.scrollIntoView({block:'center'});const r=b.getBoundingClientRect();return{x:r.x+r.width/2,y:r.y+r.height/2};})()`);await click(point.x,point.y);}
async function world(x,y){return evaluate(`(()=>{const s=document.querySelector('.editor-canvas svg');s.scrollIntoView({block:'center'});const p=new DOMPoint(${x},${-y}).matrixTransform(s.getScreenCTM());return{x:p.x,y:p.y};})()`);}
async function clickWorld(x,y,modifiers=0){const p=await world(x,y);await click(p.x,p.y,modifiers);}
async function drag(a,b){const start=await world(...a),end=await world(...b);await rpc('Input.dispatchMouseEvent',{type:'mousePressed',...start,button:'left',clickCount:1});for(let i=1;i<=10;i++){await rpc('Input.dispatchMouseEvent',{type:'mouseMoved',x:start.x+(end.x-start.x)*i/10,y:start.y+(end.y-start.y)*i/10,buttons:1,button:'left'});await settle();}await rpc('Input.dispatchMouseEvent',{type:'mouseReleased',...end,button:'left',clickCount:1});await settle();}
async function key(key,code,modifiers=0){await rpc('Input.dispatchKeyEvent',{type:'keyDown',key,code,modifiers});await rpc('Input.dispatchKeyEvent',{type:'keyUp',key,code,modifiers});await settle();}
async function node(id){return evaluate(`(()=>{const c=document.querySelector('g[data-kind=node][data-id="${id}"] circle');return c?{x:Number(c.getAttribute('cx')),y:-Number(c.getAttribute('cy'))}:null;})()`);}
async function save(){await evaluate('document.querySelector(".editor-canvas svg").focus()');await key('s','KeyS',2);return evaluate('JSON.parse(localStorage.getItem("agv-web-plans-v1"))[0]');}
function check(name,fn){assert.ok(fn,name);checks.push(name);console.log('PASS '+name);}
async function shot(name){const r=await rpc('Page.captureScreenshot',{format:'png',captureBeyondViewport:true});writeFileSync(resolve(evidence,name+'.png'),Buffer.from(r.data,'base64'));}

try{
 await wait(()=>existsSync(resolve(profile,'DevToolsActivePort')),'Browser did not expose its isolated debug port');
 const port=readFileSync(resolve(profile,'DevToolsActivePort'),'utf8').split('\n')[0];
 const pages=await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();const page=pages.find(p=>p.type==='page');assert.ok(page);
 ws=new WebSocket(page.webSocketDebuggerUrl);await new Promise((ok,no)=>{ws.addEventListener('open',ok,{once:true});ws.addEventListener('error',no,{once:true});});
 ws.addEventListener('message',event=>{const r=JSON.parse(event.data);if(r.id){const p=pending.get(r.id);if(p){pending.delete(r.id);r.error?p.no(new Error(JSON.stringify(r.error))):p.ok(r.result);}}else if(r.method==='Runtime.exceptionThrown')errors.push(r.params.exceptionDetails.exception?.description||r.params.exceptionDetails.text);});
 await rpc('Page.enable');await rpc('Runtime.enable');await rpc('Emulation.setDeviceMetricsOverride',{width:1680,height:1100,deviceScaleFactor:1,mobile:false});await rpc('Page.navigate',{url:target});
 await wait(()=>evaluate('Boolean(document.querySelector(".task-route-map svg"))'),'Task route map did not appear');
 check('selected PLC step links the preceding original transport path',await evaluate('document.querySelector(".task-route-map").textContent.includes("JackLoad")&&document.querySelector(".task-route-map").textContent.includes("LM1 → LM2")'));
 check('original curved road is highlighted rather than a new shortest route',await evaluate('[...document.querySelectorAll(".task-route-map path")].filter(p=>p.getAttribute("stroke")==="#47e4b3").length===1'));
 check('viewport includes the entire curved geometry above its endpoints',await evaluate('(()=>{const svg=document.querySelector(".task-route-map svg"),v=svg.viewBox.baseVal,p=[...svg.querySelectorAll("path")].find(p=>p.getAttribute("stroke")==="#47e4b3"),b=p.getBBox();return b.y>=v.y&&b.x>=v.x&&b.y+b.height<=v.y+v.height&&b.x+b.width<=v.x+v.width;})()'));
 await shot('task-route-plc');
 check('current PLC conditions retain unknown quality instead of synthetic success',await evaluate('document.body.textContent.includes("未知")'));
 check('no uncaught browser exceptions',errors.length===0);
 writeFileSync(resolve(evidence,'results.json'),JSON.stringify({passed:checks.length,checks,errors},null,2));console.log(`PASS ${checks.length} runtime route browser checks; ${evidence}`);
}catch(error){if(ws?.readyState===1)await shot('failure').catch(()=>{});console.error(error);process.exitCode=1;}
finally{if(ws?.readyState===1)await rpc('Browser.close').catch(()=>{});ws?.close();child.kill();}
