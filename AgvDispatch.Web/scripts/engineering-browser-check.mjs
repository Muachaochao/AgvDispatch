// Exercises the real isolated WebHost and production Vue bundle. No device or dispatch service is launched.
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {existsSync,mkdirSync,readFileSync,writeFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
const base=process.env.ENGINEERING_TEST_URL??'http://127.0.0.1:5297';
assert.equal(new URL(base).hostname,'127.0.0.1');assert.notEqual(new URL(base).port,'5199');
const config=await (await fetch(base+'/api/config')).json();assert.equal(config.engineeringSimulationOnly,true,'Requires isolated engineering instance');
const evidence=resolve(root,'.artifacts','engineering-browser-'+Date.now());mkdirSync(evidence,{recursive:true});
const checks=[],errors=[];function check(label,pass){assert.ok(pass,label);checks.push(label);console.log('PASS '+label);}
for(const path of ['/api/snapshot','/api/communication','/api/resource-review','/api/recovery','/api/ws','/api/engineering-runtime/state'])check('isolated GET blocked '+path,(await fetch(base+path)).status===403);
for(const path of ['/api/communication','/api/resource-review/submit','/api/navigation-test/submit','/api/tasks','/api/recovery/release','/api/engineering-runtime/preview','/api/engineering-runtime/apply'])check('isolated POST blocked '+path,(await fetch(base+path,{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'})).status===403);
check('cross-origin write rejected',(await fetch(base+'/api/engineering/validate',{method:'POST',headers:{Origin:'https://invalid.example','Content-Type':'application/json'},body:'{}'})).status===403);
const binary=['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe','C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);assert.ok(binary);
const profile=resolve(evidence,'profile');mkdirSync(profile);
const child=spawn(binary,['--headless=new','--disable-gpu','--no-first-run','--no-default-browser-check','--remote-debugging-address=127.0.0.1','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{windowsHide:true,stdio:'ignore'});
const sleep=ms=>new Promise(r=>setTimeout(r,ms));let ws,seq=0;const pending=new Map();
async function wait(fn,label,timeout=20000){const until=Date.now()+timeout;while(Date.now()<until){if(await fn())return;await sleep(80);}throw new Error(label);}
function rpc(method,params={}){return new Promise((ok,no)=>{const id=++seq,timer=setTimeout(()=>{pending.delete(id);no(new Error('Timeout '+method));},20000);pending.set(id,{ok:r=>{clearTimeout(timer);ok(r);},no:e=>{clearTimeout(timer);no(e);}});ws.send(JSON.stringify({id,method,params}));});}
async function evaluate(expression){const r=await rpc('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(r.exceptionDetails.exception?.description??r.exceptionDetails.text);return r.result.value;}
async function button(label){await evaluate(`(()=>{const b=[...document.querySelectorAll('button')].find(b=>b.textContent.trim()===${JSON.stringify(label)}&&b.getClientRects().length);if(!b||b.disabled)throw new Error('Missing/disabled '+${JSON.stringify(label)});b.click();})()`);await sleep(100);}
async function tab(name){await evaluate(`location.hash='/engineering?tab=${name}'`);await sleep(120);}
async function shot(name){const image=await rpc('Page.captureScreenshot',{format:'png',captureBeyondViewport:true});writeFileSync(resolve(evidence,name+'.png'),Buffer.from(image.data,'base64'));}
try{
 await wait(()=>existsSync(resolve(profile,'DevToolsActivePort')),'browser startup');
 const port=readFileSync(resolve(profile,'DevToolsActivePort'),'utf8').split('\n')[0];
 const page=(await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(x=>x.type==='page');
 ws=new WebSocket(page.webSocketDebuggerUrl);await new Promise(ok=>ws.addEventListener('open',ok,{once:true}));
 ws.addEventListener('message',e=>{const r=JSON.parse(e.data);if(r.id){const p=pending.get(r.id);if(p){pending.delete(r.id);r.error?p.no(new Error(r.error.message)):p.ok(r.result);}}else if(r.method==='Runtime.exceptionThrown')errors.push(r.params.exceptionDetails.exception?.description??r.params.exceptionDetails.text);});
 await rpc('Page.enable');await rpc('Runtime.enable');await rpc('Emulation.setDeviceMetricsOverride',{width:1600,height:1000,deviceScaleFactor:1,mobile:false});
 await rpc('Page.navigate',{url:base+'/#/engineering'});await wait(()=>evaluate(`document.querySelector('.engineering-message')?.textContent.includes('已读取')`),'draft load');
 // Accept discard prompts only in this disposable test workspace.
 await evaluate('window.confirm=()=>true');
 await button('载入示例到编辑区');await wait(()=>evaluate(`document.querySelector('.engineering-message')?.textContent.includes('示例已载入')`),'sample');
 await button('保存工程草稿');await wait(()=>evaluate(`document.querySelector('.engineering-message')?.textContent.includes('草稿已保存')`),'save');
 const draft=await (await fetch(base+'/api/engineering/draft')).json();check('300 stations and 5 vehicles saved by UI',draft.plan.stations.length===300&&draft.plan.vehicles.length===5);
 await tab('stations');check('station table paginates',await evaluate(`document.querySelector('.engineering-content').textContent.includes('第1 / 12页')`));
 const rows=draft.plan.stations.slice(0,2).map((s,i)=>({...s,name:'批量导入工位'+(i+1)}));const columns=Object.keys(rows[0]);
 const csv='\uFEFF'+[columns.join(','),...rows.map(r=>columns.map(k=>'"'+String(r[k]).replaceAll('"','""')+'"').join(','))].join('\r\n');
 const file=resolve(evidence,'stations.csv');writeFileSync(file,csv);
 const dom=await rpc('DOM.getDocument');const input=await rpc('DOM.querySelector',{nodeId:dom.root.nodeId,selector:'.engineering-content input[type=file][accept=".csv"]'});await rpc('DOM.setFileInputFiles',{nodeId:input.nodeId,files:[file]});
 await wait(()=>evaluate(`document.querySelector('.engineering-import')?.textContent.includes('修改2')`),'CSV diff');await shot('01-import-diff');await button('确认合并到编辑区');
 await button('保存工程草稿');await wait(()=>evaluate(`document.querySelector('.engineering-message')?.textContent.includes('草稿已保存')`),'merged save');
 const merged=await (await fetch(base+'/api/engineering/draft')).json();check('CSV applies two changes and preserves remaining 298',merged.plan.stations.length===300&&merged.plan.stations.filter(s=>s.name.startsWith('批量导入')).length===2);
 await button('统一校验');await wait(()=>evaluate(`document.querySelector('.engineering-content').textContent.includes('静态配置通过')`),'validation');await shot('02-validation');
 await tab('versions');await evaluate(`(()=>{const e=document.querySelector('.engineering-note textarea');e.value='浏览器验收：300工位、5车，无设备';e.dispatchEvent(new Event('input',{bubbles:true}));})()`);await button('校验并发布工程版本');
 await wait(()=>evaluate(`document.querySelector('.engineering-message')?.textContent.includes('版本已发布')`),'publish');await button('测试所选版本');await button('运行隔离模拟');
 await wait(()=>evaluate(`document.querySelector('.engineering-message')?.textContent.includes('模拟已完成')`),'simulate',45000);await shot('03-simulation');
 check('browser shows 300 completed model tasks',await evaluate(`document.querySelector('.engineering-stats').textContent.includes('300 / 300')`));
 const versions=await (await fetch(base+'/api/engineering/versions')).json();const version=versions.find(v=>v.note==='浏览器验收：300工位、5车，无设备');assert.ok(version);
 const stale=await fetch(base+'/api/engineering/draft',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({expectedRevision:'stale',plan:draft.plan})});check('stale browser tab cannot overwrite draft',stale.status===409);
 await rpc('Page.reload');await wait(()=>evaluate(`document.querySelector('.engineering-message')?.textContent.includes('已读取')`),'reload');await evaluate('window.confirm=()=>true');await tab('simulation');
 await evaluate(`(()=>{const e=document.querySelector('.engineering-form select');e.value=${JSON.stringify(version.id)};e.dispatchEvent(new Event('change',{bubbles:true}));const i=[...document.querySelectorAll('input')].find(e=>e.placeholder==='DUMP-AREA');i.value='DUMP-AREA';i.dispatchEvent(new Event('input',{bubbles:true}));})()`);
 await button('运行隔离模拟');await wait(()=>evaluate(`document.querySelector('.engineering-message')?.textContent.includes('模拟已完成')`),'blocked simulation',45000);
 check('blocked dump resource explains why no task assigned',await evaluate(`document.querySelector('.engineering-content').textContent.includes('倒料区域或等待位已封锁')`));await shot('04-blocked');
 await tab('stations');await rpc('Emulation.setDeviceMetricsOverride',{width:900,height:1100,deviceScaleFactor:1,mobile:false});await shot('05-narrow-layout');
 check('narrow layout keeps engineering panel within viewport',await evaluate(`document.querySelector('.engineering-content').getBoundingClientRect().right<=innerWidth+2`));
 await evaluate(`location.hash='/engineering-runtime?version=${version.id}'`);await wait(()=>evaluate(`document.querySelector('.runtime-page')`),'runtime page');await sleep(300);await shot('06-runtime-isolation');
 check('runtime integration page explains isolated mode',await evaluate(`document.querySelector('.runtime-page').textContent.includes('隔离')`));
 const requests=await evaluate(`performance.getEntriesByType('resource').filter(e=>e.name.includes('/api/')).map(e=>new URL(e.name).pathname)`);
 check('Vue engineering flow only used engineering and config APIs',requests.every(p=>p==='/api/config'||p.startsWith('/api/engineering/')));
 assert.deepEqual(errors,[]);check('no browser runtime exceptions',true);
 writeFileSync(resolve(evidence,'checks.json'),JSON.stringify({checks,errors,versionId:version.id,base},null,2));console.log('Evidence: '+evidence);
}finally{ws?.close();child.kill();}
