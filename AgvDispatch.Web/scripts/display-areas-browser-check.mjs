// Local browser integration check. Uses a separate disposable profile and synthetic data only.
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
const target=process.env.AREAS_TEST_URL;
assert.ok(target,'Set AREAS_TEST_URL to an isolated test server');
assert.ok(['127.0.0.1','localhost'].includes(new URL(target).hostname),'Only loopback test servers are allowed');
assert.ok(!['5199','5198'].includes(new URL(target).port),'Never run writable fixture checks on daily ports');
const binary=process.env.EDITOR_TEST_BROWSER||['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe','C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert.ok(binary,'Set EDITOR_TEST_BROWSER to an installed Chromium browser');
const profile=resolve(root,'.codex-tmp',`areas-browser-${Date.now()}`),evidence=resolve(root,'AgvDispatch.Web/evidence/display-areas');mkdirSync(profile,{recursive:true});mkdirSync(evidence,{recursive:true});
const child=spawn(binary,['--headless=new','--disable-gpu','--no-first-run','--no-default-browser-check','--remote-debugging-address=127.0.0.1','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{windowsHide:true,stdio:'ignore'});
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
let ws,seq=0;const pending=new Map(),errors=[],checks=[];
async function wait(fn,message,timeout=15000){const until=Date.now()+timeout;while(Date.now()<until){if(await fn())return;await sleep(80);}throw new Error(message);}
function rpc(method,params={}){return new Promise((ok,no)=>{const id=++seq,timer=setTimeout(()=>{pending.delete(id);no(new Error(`Timeout ${method}`));},15000);pending.set(id,{ok:v=>{clearTimeout(timer);ok(v);},no:e=>{clearTimeout(timer);no(e);}});ws.send(JSON.stringify({id,method,params}));});}
async function evaluate(expression){const r=await rpc('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(r.exceptionDetails.text+' '+r.exceptionDetails.exception?.description);return r.result.value;}
async function settle(){await evaluate('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');}
async function click(x,y,modifiers=0){await rpc('Input.dispatchMouseEvent',{type:'mouseMoved',x,y});await rpc('Input.dispatchMouseEvent',{type:'mousePressed',x,y,button:'left',clickCount:1,modifiers});await rpc('Input.dispatchMouseEvent',{type:'mouseReleased',x,y,button:'left',clickCount:1,modifiers});await settle();}
async function button(text){const point=await evaluate(`(()=>{const b=[...document.querySelectorAll('button')].find(b=>b.textContent.trim().replace(/ \\*$/,\"\")===${JSON.stringify(text)}&&b.getClientRects().length);if(!b)throw new Error('Button missing: '+${JSON.stringify(text)});if(b.disabled)throw new Error('Button disabled: '+${JSON.stringify(text)});b.scrollIntoView({block:'center'});const r=b.getBoundingClientRect();return{x:r.x+r.width/2,y:r.y+r.height/2};})()`);await click(point.x,point.y);if(text==='保存展示分区')await wait(()=>evaluate('document.querySelector(".display-area-editor [role=status]").textContent.includes("已保存")'),'save completes');}
async function world(x,y){return evaluate(`(()=>{const s=document.querySelector('.display-area-canvas');s.scrollIntoView({block:'center'});const p=new DOMPoint(${x},${-y}).matrixTransform(s.getScreenCTM());return{x:p.x,y:p.y};})()`);}
async function clickWorld(x,y,modifiers=0){const p=await world(x,y);await click(p.x,p.y,modifiers);}
async function drag(a,b){const start=await world(...a),end=await world(...b);await rpc('Input.dispatchMouseEvent',{type:'mousePressed',...start,button:'left',clickCount:1});for(let i=1;i<=10;i++){await rpc('Input.dispatchMouseEvent',{type:'mouseMoved',x:start.x+(end.x-start.x)*i/10,y:start.y+(end.y-start.y)*i/10,buttons:1,button:'left'});await settle();}await rpc('Input.dispatchMouseEvent',{type:'mouseReleased',...end,button:'left',clickCount:1});await settle();}
async function key(key,code,modifiers=0){await rpc('Input.dispatchKeyEvent',{type:'keyDown',key,code,modifiers});await rpc('Input.dispatchKeyEvent',{type:'keyUp',key,code,modifiers});await settle();}
async function node(id){return evaluate(`(()=>{const c=document.querySelector('g[data-kind=node][data-id="${id}"] circle');return c?{x:Number(c.getAttribute('cx')),y:-Number(c.getAttribute('cy'))}:null;})()`);}
async function save(){await evaluate('document.querySelector(".display-area-canvas").focus()');await key('s','KeyS',2);return evaluate('JSON.parse(localStorage.getItem("agv-web-plans-v1"))[0]');}
function check(name,fn){assert.ok(fn,name);checks.push(name);console.log('PASS '+name);}
async function shot(name){const r=await rpc('Page.captureScreenshot',{format:'png',captureBeyondViewport:true});writeFileSync(resolve(evidence,name+'.png'),Buffer.from(r.data,'base64'));}

try{
 await wait(()=>existsSync(resolve(profile,'DevToolsActivePort')),'Browser did not expose debug port');
 const port=readFileSync(resolve(profile,'DevToolsActivePort'),'utf8').split('\n')[0];
 const pages=await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
 ws=new WebSocket(pages.find(p=>p.type==='page').webSocketDebuggerUrl);
 await new Promise((ok,no)=>{ws.addEventListener('open',ok,{once:true});ws.addEventListener('error',no,{once:true});});
 ws.addEventListener('message',event=>{const r=JSON.parse(event.data);if(r.id){const p=pending.get(r.id);if(p){pending.delete(r.id);r.error?p.no(new Error(JSON.stringify(r.error))):p.ok(r.result);}}else if(r.method==='Runtime.exceptionThrown')errors.push(r.params.exceptionDetails.exception?.description||r.params.exceptionDetails.text);});
 await rpc('Page.enable');await rpc('Runtime.enable');
 await rpc('Emulation.setDeviceMetricsOverride',{width:1680,height:1100,deviceScaleFactor:1,mobile:false});
 await rpc('Page.navigate',{url:target});
 await wait(()=>evaluate('document.readyState==="complete"'),'initial page loads');
 await evaluate('(async()=>{const c=await(await fetch("/api/maps/UI-ACTIVE/display-areas")).json();if(c.document){const r=await fetch("/api/maps/UI-ACTIVE/display-areas",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({expectedRevision:c.document.revision,basisHash:c.basisHash,areas:[]})});if(!r.ok)throw new Error("Cannot clear isolated fixture");}})()');
 await rpc('Page.navigate',{url:target+'#/planning?tab=areas'});
 await wait(()=>evaluate('Boolean(document.querySelector(".display-area-canvas"))'),'Display area editor mounted');
 check('new dedicated tab visible',await evaluate('[...document.querySelectorAll(".workbench-tabs button")].some(b=>b.textContent.includes("展示区域"))'));
 await button('新增区域 · 矩形框选');await drag([-1,-1],[2,2]);
 check('rectangle suggests only enclosed LM1',await evaluate('[...document.querySelectorAll(".area-layout aside:last-child input[type=checkbox]")].filter(e=>e.checked).length===3'));
 await evaluate('(()=>{const e=document.querySelector(".area-layout aside:last-child input:not([type=checkbox]):not([type=color])");e.value="上料区";e.dispatchEvent(new Event("input",{bubbles:true}));})()');
 await button('保存展示分区');
 await wait(()=>evaluate('document.querySelector(".display-area-editor [role=status]").textContent.includes("已保存")'),'save succeeds');
 let data=await evaluate('(async()=>{return (await fetch("/api/maps/UI-ACTIVE/display-areas")).json()})()');
 check('save contains exact membership and independent map basis',data.document.areas[0].name==='上料区'&&data.document.areas[0].stationIds.join()==='取货点');
 const rev=data.document.revision;
 await button('新增区域 · 多边形');for(const p of [[6,-.5],[8.5,-.5],[8.5,6.5],[6,6.5]])await clickWorld(...p);await button('完成多边形');
 check('polygon suggests two stations',await evaluate('document.querySelector(".area-layout aside:last-child").textContent.includes("包含站点 2")'));
 await evaluate('(()=>{const e=document.querySelector(".area-layout aside:last-child input:not([type=checkbox]):not([type=color])");e.value="下料区";e.dispatchEvent(new Event("input",{bubbles:true}));})()');
 await button('保存展示分区');
 data=await evaluate('(async()=>{return (await fetch("/api/maps/UI-ACTIVE/display-areas")).json()})()');
 check('second saved area preserves first',data.document.areas.length===2&&data.document.areas[0].stationIds.join()==='取货点');
 const conflict=await evaluate(`(async()=>{const c=await (await fetch('/api/maps/UI-ACTIVE/display-areas')).json();return (await fetch('/api/maps/UI-ACTIVE/display-areas',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({expectedRevision:${JSON.stringify(rev)},basisHash:c.basisHash,areas:c.document.areas})})).status})()`);
 check('API rejects stale concurrent revision',conflict===409);
 const invalid=await evaluate(`(async()=>{const c=await (await fetch('/api/maps/UI-ACTIVE/display-areas')).json();c.document.areas[0].stationIds=['missing'];return (await fetch('/api/maps/UI-ACTIVE/display-areas',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({expectedRevision:c.document.revision,basisHash:c.basisHash,areas:c.document.areas})})).status})()`);
 check('API rejects nonexistent station without altering saved areas',invalid===400);
 await rpc('Page.reload');
 await wait(()=>evaluate('document.querySelectorAll(".display-area-canvas [data-area-id]").length===2'),'saved regions reload');
 check('both regions survive browser reload',true);
 await button('下料区 · 2站点');
 await drag([6,-.5],[5,-1]);
 await evaluate('(()=>{const inputs=[...document.querySelectorAll(".area-layout aside:last-child label")];const l=inputs.find(l=>l.textContent.includes("LM1"));l.querySelector("input").click();})()');
 await button('保存展示分区');
 data=await evaluate('(async()=>{return (await fetch("/api/maps/UI-ACTIVE/display-areas")).json()})()');
 check('vertex movement and manual membership persist',data.document.areas[1].boundary[0].x<5.1&&data.document.areas[1].stationIds.includes('取货点'));
 await shot('editor');
 await evaluate('location.hash="#/"');
 await wait(()=>evaluate('document.querySelectorAll(".display-area-label").length===2'),'dashboard labels render');
 check('dashboard shows both labels',true);
 await button('上料区');
 check('click label shows member station',await evaluate('[...document.querySelectorAll(".resource-location")].some(p=>p.textContent.includes("包含站点")&&p.textContent.includes("LM1"))'));
 await button('3D');await shot('dashboard-3d');
 check('area remains visible in 3D',await evaluate('document.querySelectorAll(".display-area-label:not([hidden])").length>0'));
 await evaluate('location.hash="#/planning?tab=areas"');
 await wait(()=>evaluate('document.querySelectorAll(".display-area-canvas [data-area-id]").length===2'),'editor restored');
 await button('下料区 · 3站点');await button('删除此区域标注');await button('保存展示分区');
 data=await evaluate('(async()=>{return (await fetch("/api/maps/UI-ACTIVE/display-areas")).json()})()');
 check('delete leaves stations and roads unchanged',data.document.areas.length===1&&data.map.nodes.length===3&&data.map.edges.length===2);
 check('no browser runtime exceptions',errors.length===0);
 writeFileSync(resolve(evidence,'result.json'),JSON.stringify({checks,errors},null,2));
 console.log(checks.length+' browser checks passed');
}catch(error){await shot('failure').catch(()=>{});writeFileSync(resolve(evidence,'failure.txt'),String(error.stack||error));console.error(error);process.exitCode=1;}
finally{if(ws?.readyState===1)await rpc('Browser.close').catch(()=>{});ws?.close();child.kill();}
