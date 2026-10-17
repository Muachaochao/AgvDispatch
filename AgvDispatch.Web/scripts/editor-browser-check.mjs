// Local browser integration check. Uses a separate disposable profile and synthetic data only.
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
const target=process.env.EDITOR_TEST_URL||'http://127.0.0.1:5199/#/planning';
assert.ok(['127.0.0.1','localhost'].includes(new URL(target).hostname),'Only loopback test servers are allowed');
const binary=process.env.EDITOR_TEST_BROWSER||['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe','C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert.ok(binary,'Set EDITOR_TEST_BROWSER to an installed Chromium browser');
const profile=resolve(root,'.codex-tmp',`editor-browser-${Date.now()}`),evidence=resolve(root,'AgvDispatch.Web/evidence/editor-interactions');mkdirSync(profile,{recursive:true});mkdirSync(evidence,{recursive:true});
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
 await wait(()=>evaluate('Boolean(document.querySelector(".editor-canvas svg"))'),'Editor did not mount');
 await button('演示模式');await settle();
 await button('节点');for(const p of [[2,3],[12,3],[12,13],[2,13]])await clickWorld(...p);
 check('draws four nodes through the canvas',await evaluate('document.querySelectorAll("g[data-kind=node]").length===4'));
 await button('选择 / 框选');await drag([2,3],[3,4]);check('node dragging snaps to grid',JSON.stringify(await node('P1'))===JSON.stringify({x:3,y:4}));
 await evaluate('document.querySelector(".editor-canvas svg").focus()');await key('z','KeyZ',2);check('Ctrl+Z undoes the entire drag once',(await node('P1')).x===2);await key('y','KeyY',2);check('Ctrl+Y restores the drag',(await node('P1')).x===3);
 await clickWorld(3,4);await clickWorld(12,3,2);check('Ctrl click selects multiple nodes',await evaluate('document.querySelector(".editor-coordinate").textContent.includes("已选 2")'));
 await button('批量 / 对齐 / 拓扑');await button('自动顺序连接');check('topology tool creates a road',await evaluate('document.querySelectorAll("g[data-kind=edge]").length===1'));
 await button('圆弧');await clickWorld(3,4);await clickWorld(12,3);await clickWorld(7,9);check('arc drawing creates an SVG arc',await evaluate('[...document.querySelectorAll("g[data-kind=edge] path")].some(p=>p.getAttribute("d").includes(" A "))'));
 await button('贝塞尔');await clickWorld(12,3);await clickWorld(12,13);await clickWorld(16,5);await clickWorld(16,11);check('Bezier drawing creates cubic geometry',await evaluate('[...document.querySelectorAll("g[data-kind=edge] path")].some(p=>p.getAttribute("d").includes(" C "))'));
 await button('选择 / 框选');await clickWorld(15,8);await drag([16,5],[17,6]);let curvePlan=await save();check('curve control handles can be dragged',curvePlan.definition.edges.find(e=>e.id==='E3').controlPoints[0].x===17);
 await button('批量 / 对齐 / 拓扑');await button('反转起终点');curvePlan=await save();check('reverse direction swaps curve endpoints and control order',curvePlan.definition.edges.find(e=>e.id==='E3').from==='P3'&&curvePlan.definition.edges.find(e=>e.id==='E3').controlPoints[0].y===11);
 await button('拆分所选道路（保留曲线）');const splitPlan=await save();check('curve splitting adds a shared node and two connected curves',splitPlan.definition.nodes.length===5&&splitPlan.definition.edges.length===4);await key('z','KeyZ',2);check('curve split is undone in one step',(await save()).definition.nodes.length===4);
 await button('区域边界');for(const p of [[5,5],[9,5],[9,8],[5,8]])await clickWorld(...p);await key('Enter','Enter');check('Enter finishes a four-vertex resource',await evaluate('document.querySelectorAll("polygon[data-kind=resource]").length===1'));
 await button('测量');await clickWorld(2,3);await clickWorld(12,3);check('measurement reports metres',await evaluate('document.querySelector(".editor-tool-hint").textContent.includes("10.000 m")'));
 await key('Escape','Escape');await drag([1,2],[14,15]);check('marquee selects multiple objects',await evaluate('!document.querySelector(".editor-coordinate").textContent.includes("已选 0")&&!document.querySelector(".editor-coordinate").textContent.includes("已选 1 ·")'));
 await evaluate('document.querySelector(".editor-canvas svg").focus()');const before=await save();await key('c','KeyC',2);await key('v','KeyV',2);const copied=await save();check('Ctrl+C/V clones nodes and roads with valid references',copied.definition.nodes.length>before.definition.nodes.length&&copied.definition.edges.every(e=>copied.definition.nodes.some(n=>n.id===e.from)&&copied.definition.nodes.some(n=>n.id===e.to)));
 await key('Delete','Delete');const deleted=await save();check('Delete removes the copied group and dependent roads',deleted.definition.nodes.length===before.definition.nodes.length);
 await button('关联刷');await evaluate('(()=>{const s=document.querySelector(".editor-options select:last-of-type");const selectors=[...document.querySelectorAll(".editor-options select")];const v=selectors.find(s=>[...s.options].some(o=>o.value==="R1"));v.value="R1";v.dispatchEvent(new Event("change",{bubbles:true}));})()');await clickWorld(3,4);const brushed=await save();check('resource brush adds the selected association',brushed.definition.nodes.find(n=>n.id==='P1').resourceIds.includes('R1'));
 await button('选择 / 框选');await clickWorld(12,3);await button('图层');await button('新增图层');check('layer creation is visible in layer manager',await evaluate('document.querySelectorAll(".layer-card").length===2'));
 await button('批量');await button('移至当前图层');await button('图层');await evaluate('(()=>{const card=document.querySelector(".layer-card.current");const label=[...card.querySelectorAll("label")].find(l=>l.textContent.trim()==="锁定");const c=label.querySelector("input");c.checked=true;c.dispatchEvent(new Event("change",{bubbles:true}));})()');
 await button('选择 / 框选');const pos=await node('P2');await drag([pos.x,pos.y],[pos.x+2,pos.y+2]);check('locked layer blocks canvas dragging',JSON.stringify(await node('P2'))===JSON.stringify(pos));
 const locked=await save();check('saved layers retain locking and current layer settings',locked.definition.layers.some(l=>l.isLocked)&&locked.definition.editorSettings.currentLayerId==='layer-1');
 await button('图层');await evaluate('(()=>{const card=document.querySelector(".layer-card.current");const label=[...card.querySelectorAll("label")].find(l=>l.textContent.trim()==="显示");const c=label.querySelector("input");c.checked=false;c.dispatchEvent(new Event("change",{bubbles:true}));})()');await settle();check('hidden layer removes its nodes from the canvas',await node('P2')===null);
 await evaluate('(()=>{const card=document.querySelector(".layer-card.current");for(const [name,value] of [["显示",true],["锁定",false]]){const c=[...card.querySelectorAll("label")].find(l=>l.textContent.trim()===name).querySelector("input");c.checked=value;c.dispatchEvent(new Event("change",{bubbles:true}));}})()');await settle();
 await evaluate('(()=>{const b=[...document.querySelector(".layer-card.current").querySelectorAll("button")].find(b=>b.textContent==="删除并迁移对象");b.click();})()');const migrated=await save();check('layer deletion migrates objects and persists the surviving current layer',migrated.definition.layers.length===1&&migrated.definition.nodes.find(n=>n.id==='P2').layerId==='navigation'&&migrated.definition.editorSettings.currentLayerId==='navigation');
 await button('历史');check('history panel records editable operations',await evaluate('document.querySelectorAll(".editor-history li").length>8'));
 await button('贝塞尔');await clickWorld(3,4);await key('Escape','Escape');check('Escape cancels an unfinished drawing',await evaluate('document.querySelector(".editor-tool-palette button.active").textContent==="选择 / 框选"'));
 await save();
 await button('适配');await evaluate('document.querySelector(".planning-workbench").scrollTop=0;document.querySelector(".editor-inspector").scrollTop=0');await settle();await shot('desktop');await rpc('Emulation.setDeviceMetricsOverride',{width:1100,height:820,deviceScaleFactor:1,mobile:false});await settle();await shot('laptop');
 check('no uncaught browser exceptions',errors.length===0);writeFileSync(resolve(evidence,'results.json'),JSON.stringify({passed:checks.length,checks,errors},null,2));console.log(`PASS ${checks.length} browser interaction checks; ${evidence}`);
}catch(error){if(ws?.readyState===1)await shot('failure').catch(()=>{});writeFileSync(resolve(evidence,'failure.txt'),String(error.stack||error));console.error(error);process.exitCode=1;}
finally{if(ws?.readyState===1)await rpc('Browser.close').catch(()=>{});ws?.close();child.kill();}
