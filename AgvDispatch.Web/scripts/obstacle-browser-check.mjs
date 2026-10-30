// Isolated browser checks with synthetic HTTP/WebSocket data. Never connects to equipment.
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const target = process.env.OBSTACLE_TEST_URL;
assert.ok(target && ['127.0.0.1', 'localhost'].includes(new URL(target).hostname), 'Set isolated loopback OBSTACLE_TEST_URL');
assert.notEqual(new URL(target).port, '5199', 'Do not intercept the daily instance');
const browser = process.env.EDITOR_TEST_BROWSER || ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert.ok(browser, 'Chromium browser is required');
const profile = resolve(root, '.codex-tmp', `obstacle-browser-${Date.now()}`);
const evidence = resolve(root, 'AgvDispatch.Web/evidence/obstacle-interactions');
mkdirSync(profile, { recursive: true }); mkdirSync(evidence, { recursive: true });
const child = spawn(browser, ['--headless=new', '--enable-unsafe-swiftshader', '--use-angle=swiftshader', '--no-first-run', '--no-default-browser-check', '--remote-debugging-address=127.0.0.1', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
let socket, sequence = 0;
const pending = new Map(), checks = [], errors = [];
async function wait(fn, message) { const end = Date.now() + 20000; while (Date.now() < end) { if (await fn()) return; await sleep(100); } throw new Error(message); }
function rpc(method, params = {}) { return new Promise((resolve, reject) => { const id = ++sequence; const timer = setTimeout(() => { pending.delete(id); reject(new Error(`Timeout ${method}`)); }, 20000); pending.set(id, { resolve: x => { clearTimeout(timer); resolve(x); }, reject }); socket.send(JSON.stringify({ id, method, params })); }); }
async function evaluate(expression) { const r = await rpc('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true }); if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text); return r.result.value; }
async function click(text) { await evaluate(`(()=>{const b=[...document.querySelectorAll('button')].find(b=>b.textContent.trim()===${JSON.stringify(text)}&&b.getClientRects().length);if(!b||b.disabled)throw new Error('Missing or disabled '+${JSON.stringify(text)});b.click();})()`); }
async function select(label, value) { await evaluate(`(()=>{const l=[...document.querySelectorAll('label')].find(l=>l.textContent.trim().startsWith(${JSON.stringify(label)})&&l.getClientRects().length);const s=l?.querySelector('select');if(!s)throw new Error('Missing select '+${JSON.stringify(label)});s.value=${JSON.stringify(value)};s.dispatchEvent(new Event('change',{bubbles:true}));})()`); }
async function input(label, value) { await evaluate(`(()=>{const l=[...document.querySelectorAll('label')].find(l=>l.textContent.trim().startsWith(${JSON.stringify(label)})&&l.getClientRects().length);const i=l?.querySelector('input');if(!i)throw new Error('Missing input '+${JSON.stringify(label)});if(i.type==='checkbox'){i.checked=${!!value};i.dispatchEvent(new Event('change',{bubbles:true}));}else{i.value=${JSON.stringify(value)};i.dispatchEvent(new Event('input',{bubbles:true}));}})()`); }
async function disabled(text) { return evaluate(`(()=>{const b=[...document.querySelectorAll('button')].find(b=>b.textContent.trim()===${JSON.stringify(text)}&&b.getClientRects().length);return !b||b.disabled;})()`); }
function check(name, value) { assert.ok(value, name); checks.push(name); console.log('PASS ' + name); }
async function shot(name) { const r = await rpc('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true }); writeFileSync(resolve(evidence, `${name}.png`), Buffer.from(r.data, 'base64')); }

const fixture = `(() => {
 const at=()=>new Date().toISOString();
 const map={id:'TRIANGLE',version:'V1',definitionHash:'abc',name:'合成三角测试地图',unit:'m',coordinateSystem:'local',warnings:[],resources:[],nodes:[{id:'LM1',x:0,y:0,enabled:true,type:'Station'},{id:'LM2',x:2,y:2,enabled:true,type:'Station'},{id:'LM3',x:4,y:0,enabled:true,type:'Station'}],edges:[{id:'E13',from:'LM1',to:'LM3',enabled:true,bidirectional:true,kind:'Line',points:[]},{id:'E12',from:'LM1',to:'LM2',enabled:true,bidirectional:true,kind:'Line',points:[]},{id:'E23',from:'LM2',to:'LM3',enabled:true,bidirectional:true,kind:'Line',points:[]}]};
 const plan={available:true,message:'已排除E13，候选经LM2',closureRevision:1,mapId:map.id,versionId:map.version,definitionHash:'abc',robotMap:'robot-map',robotMapMd5:'md5',sourceSite:'LM1',targetSite:'LM3',route:['LM1','LM2','LM3'],blockedEdgeIds:['E13']};
 window.obstacleFixture={map,plan,posts:[],revision:0,closures:[],noRoute:false,current:{taskId:'ORIGINAL',vendorTaskId:'OLD',vehicleId:'FORK',sourceSite:'LM1',targetSite:'LM3',robotMap:'robot-map',robotMapMd5:'md5',state:'Running',message:'合成受阻等待',updatedAt:at(),route:['LM1','LM3'],currentStation:'LM1',x:0,y:0,observedAt:at(),detour:null}};
 const f=window.obstacleFixture;
 function snapshot(){return{receivedAt:at(),connected:true,source:'backend',error:null,map:f.map,positionedVehicleIds:[],status:{generatedAt:at(),service:{version:'synthetic',startedAt:'2026-10-30T00:00:00Z'},agvs:[],safety:{dispatchingEnabled:false,plcWritesAllowed:false,agvCommandsAllowed:false},plc:{state:'Unknown',goodSignals:0,totalSignals:0}},tasks:{items:[],totalCount:0},devices:{equipment:[]},resources:[],alarms:[]};}
 class SyntheticSocket{constructor(){this.timer=setInterval(()=>this.onmessage?.({data:JSON.stringify(snapshot())}),300);}close(){clearInterval(this.timer);}} window.WebSocket=SyntheticSocket;
 const nativeFetch=window.fetch.bind(window); window.fetch=async(url,options={})=>{
  const path=String(url),body=options.body?JSON.parse(options.body):null;
  if(!path.startsWith('/api/'))return nativeFetch(url,options);
  if(body)f.posts.push({path,body});
  let data;
  if(path==='/api/config')data={taskSubmissionEnabled:false,taskCancellationEnabled:false};
  else if(path==='/api/road-closures'&&!body)data={ready:true,revision:f.revision,mapId:map.id,versionId:map.version,definitionHash:'abc',closures:f.closures};
  else if(path==='/api/road-closures') {f.revision++;const c={closureId:'C1',requestId:body.requestId,mapId:map.id,versionId:map.version,definitionHash:'abc',edgeId:body.edgeId,fromNodeId:body.fromNodeId,toNodeId:body.toNodeId,bidirectional:body.bidirectional,traversals:[{edgeId:'E13',fromNodeId:'LM1',toNodeId:'LM3'},{edgeId:'E13',fromNodeId:'LM3',toNodeId:'LM1'}],source:body.source,reason:body.reason,createdAt:at(),active:true,releasedAt:null,releaseReason:null};f.closures=[c];data={accepted:true,message:'已封锁',revision:f.revision,closure:c};}
  else if(path==='/api/road-closures/release'){f.revision++;f.closures[0].active=false;f.closures[0].releasedAt=at();f.closures[0].releaseReason=body.reason;data={accepted:true,message:'已解封',revision:f.revision};}
  else if(path==='/api/road-closures/preview')data={available:true,message:'备选经LM2；仅预览',revision:f.revision,mapId:map.id,versionId:map.version,definitionHash:'abc',sourceSite:body.sourceSite,targetSite:body.targetSite,route:{nodes:['LM1','LM2','LM3'],edgeIds:['E12','E23'],resourceIds:[],cost:2}};
  else if(path==='/api/navigation-test')data={ready:false,blockReason:'原测试执行中',enabled:true,vehicleId:'FORK',current:f.current,message:'合成'};
  else if(path==='/api/navigation-test/fork')data={supported:true,ready:false,blockReason:'合成原测试执行中',vehicleId:'FORK',telemetry:null,current:null};
  else if(path==='/api/navigation-test/detour/preview')data={previewId:'P1',expiresAt:new Date(Date.now()+30000).toISOString(),taskId:'ORIGINAL',vehicleId:'FORK',blockedSince:at(),plan:{...f.plan,available:!f.noRoute,route:f.noRoute?[]:f.plan.route,message:f.noRoute?'无备选道路，原任务等待':f.plan.message}};
  else if(path==='/api/navigation-test/detour/confirm'){f.current.state='DetourCancelRequested';f.current.detour={previewId:'P1',state:'AwaitingOriginalTerminal',message:'原任务未终结，等待停稳',plan:f.plan,originalVendorTaskIds:['OLD'],originalRoute:['LM1','LM3'],stoppedObservedAt:null,sendAttemptedAt:null,newVendorTaskIds:[]};data=f.current;}
  else if(path==='/api/navigation-test/detour/continue'){f.current.state='Accepted';f.current.detour.state='Executing';f.current.detour.sendAttemptedAt=at();f.current.detour.newVendorTaskIds=['NEW1','NEW2'];f.current.route=['LM1','LM2','LM3'];data=f.current;}
  else if(path==='/api/traffic/waiting')data={page:{capturedAt:at(),totalWaiting:0,totalMatched:0,pageNumber:1,pageSize:25,items:[],blockingResources:[]},mapId:map.id,mapVersion:map.version,mapDefinitionHash:'abc'};
  else if(path.includes('/display-areas'))data={applicable:true,document:{areas:[]}};
  else return new Response(JSON.stringify({error:'Unconfigured synthetic API '+path}),{status:404,headers:{'content-type':'application/json'}});
  return new Response(JSON.stringify(data),{status:200,headers:{'content-type':'application/json'}});
 };
})();`;

try {
  await wait(() => existsSync(resolve(profile, 'DevToolsActivePort')), 'Browser did not start');
  const port = readFileSync(resolve(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0];
  const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  socket = new WebSocket(pages.find(page => page.type === 'page').webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
  socket.addEventListener('message', event => { const r = JSON.parse(event.data); if (r.id) { const p = pending.get(r.id); if (p) { pending.delete(r.id); r.error ? p.reject(new Error(JSON.stringify(r.error))) : p.resolve(r.result); } } else if (r.method === 'Runtime.exceptionThrown') errors.push(r.params.exceptionDetails.exception?.description || r.params.exceptionDetails.text); });
  await rpc('Page.enable'); await rpc('Runtime.enable');
  await rpc('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1050, deviceScaleFactor: 1, mobile: false });
  await rpc('Page.addScriptToEvaluateOnNewDocument', { source: fixture });
  await rpc('Page.navigate', { url: target });
  await wait(() => evaluate('document.body.textContent.includes("管理封锁 / 预览路线")'), 'Closure panel missing');
  await click('管理封锁 / 预览路线');
  await wait(() => evaluate('document.querySelector(".closure-fields select")?.disabled===false'), 'Current closure map did not load');
  await select('地图路段', 'E13'); await input('现场原因', '合成障碍占用通道');
  check('closure requires explicit field confirmation', await disabled('封锁此通道'));
  await input('已核对地图路段', true); await click('封锁此通道');
  await wait(() => evaluate('document.querySelector(".closure-table").textContent.includes("封锁中")'), 'Closure did not appear');
  check('closure request identifies map, concrete edge and both directions', await evaluate('obstacleFixture.posts.some(p=>p.path==="/api/road-closures"&&p.body.edgeId==="E13"&&p.body.expectedVersionId==="V1"&&p.body.bidirectional===true)'));
  await select('规划起点', 'LM1'); await select('规划终点', 'LM3'); await click('预览避开封锁的路线');
  await wait(() => evaluate('document.querySelector(".closure-candidate")?.textContent.includes("LM1 → LM2 → LM3")'), 'Candidate route missing');
  check('global preview shows alternate and does not send navigation', await evaluate('!obstacleFixture.posts.some(p=>p.path.includes("navigation-test"))'));
  await evaluate('document.querySelector(".closure-candidate").scrollIntoView({block:"center"})'); await shot('closure-candidate');
  await click('核对后解封'); check('release requires clearance evidence', await disabled('记录人工解封'));
  await input('清理及复核依据', '合成测试现场通道已清理'); await input('已现场确认障碍清除', true); await click('记录人工解封');
  await wait(() => evaluate('document.querySelector(".closure-table").textContent.includes("已解除")'), 'Release audit missing');
  check('release retains record and invalidates old candidate', await evaluate('!document.querySelector(".closure-candidate")&&obstacleFixture.closures.length===1'));
  await evaluate('location.hash="/navigation-test"');
  await wait(() => evaluate('!!document.querySelector(".navigation-detour")'), 'Detour panel missing');
  await evaluate('obstacleFixture.noRoute=true'); await click('核验实际接入点并预览改道');
  await wait(() => evaluate('document.querySelector(".navigation-detour").textContent.includes("无可执行的备选路线")'), 'No route state missing');
  check('no route retains original task and cannot cancel it', await disabled('确认结束原导航，等待停稳核验'));
  await evaluate('obstacleFixture.noRoute=false'); await click('核验实际接入点并预览改道');
  await wait(() => evaluate('document.querySelector(".navigation-detour").textContent.includes("已找到备选路线")'), 'Detour preview missing');
  check('detour cancellation requires explicit confirmation', await disabled('确认结束原导航，等待停稳核验'));
  await input('已核对候选路线及现场条件', true); await click('确认结束原导航，等待停稳核验');
  await wait(() => evaluate('document.querySelector(".navigation-detour").textContent.includes("原任务未终结")'), 'Cancel barrier missing');
  check('cancel response cannot continue until original terminal and stopped', await disabled('确认执行备选路线'));
  await evaluate('obstacleFixture.current.state="DetourReady";obstacleFixture.current.detour.state="Ready"'); await click('查询原任务');
  await wait(() => evaluate('document.querySelector(".navigation-detour").textContent.includes("后台已核验原导航终结")'), 'Ready state missing');
  check('ready still requires final route confirmation', await disabled('确认执行备选路线'));
  await evaluate('obstacleFixture.current.state="ReviewHold";obstacleFixture.current.detour.state="ReviewHold"'); await click('查询原任务');
  await wait(async () => !await disabled('重新核验实际接入点与候选'), 'Restart review did not allow read-only revalidation');
  await click('重新核验实际接入点与候选');
  await wait(() => evaluate('document.querySelector(".navigation-detour").textContent.includes("已重新核验候选")'), 'Refreshed candidate missing');
  check('restart review can revalidate without silently sending', await evaluate('!obstacleFixture.posts.some(p=>p.path.endsWith("/detour/continue"))'));
  await input('已核对新候选及现场条件', true); await click('确认更新候选并核验停稳');
  await evaluate('obstacleFixture.current.state="DetourReady";obstacleFixture.current.detour.state="Ready"'); await click('查询原任务');
  await wait(() => evaluate('document.querySelector(".navigation-detour").textContent.includes("后台已核验原导航终结")'), 'Revalidated ready state missing');
  await input('已确认备选路线通行条件', true); await click('确认执行备选路线');
  await wait(() => evaluate('document.querySelector(".navigation-detour").textContent.includes("NEW1")'), 'New task chain missing');
  check('detour sends once and displays original plus new task IDs', await evaluate('obstacleFixture.posts.filter(p=>p.path.endsWith("/detour/continue")).length===1&&document.querySelector(".navigation-detour").textContent.includes("OLD")'));
  check('sent detour cannot refresh or resend its candidate', await disabled('重新核验实际接入点与候选'));
  await shot('navigation-detour');
  check('no uncaught browser exceptions', errors.length === 0);
  writeFileSync(resolve(evidence, 'results.json'), JSON.stringify({ passed: checks.length, checks, errors }, null, 2));
  console.log(`PASS ${checks.length} obstacle browser checks`);
} catch (ex) { console.error(ex); if (socket?.readyState === 1) await shot('failure').catch(() => {}); process.exitCode = 1; }
finally { if (socket?.readyState === 1) await rpc('Browser.close').catch(() => {}); socket?.close(); child.kill(); }
