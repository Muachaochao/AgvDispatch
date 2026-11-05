// Isolated mock HTTP + Chromium. Never connects to 5199, a PLC or a vehicle.
import {createServer} from 'node:http';
import {spawn} from 'node:child_process';
import {existsSync,mkdirSync,readFileSync} from 'node:fs';
import {resolve,dirname,sep} from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';

const project=resolve(dirname(fileURLToPath(import.meta.url)),'../..'),dist=resolve(project,'AgvDispatch.Web/dist');
const profile=resolve(project,'.codex-tmp',`test-center-browser-${Date.now()}`);mkdirSync(profile,{recursive:true});
const binary=['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe','C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert.ok(binary,'Chromium browser required');
let child,ws,seq=0;
const rpcPending=new Map(),errors=[];
const sleep=ms=>new Promise(ok=>setTimeout(ok,ms));
const server=createServer((req,res)=>{
 try{
  const url=new URL(req.url,'http://127.0.0.1');
  const json=(code,data)=>{res.writeHead(code,{'Content-Type':'application/json'});res.end(JSON.stringify(data));};
  if(url.pathname==='/api/config')return json(200,{taskSubmissionEnabled:true});
  if(url.pathname.startsWith('/api/'))return json(503,{error:'isolated fixture only'});
  const file=resolve(dist,'.'+(url.pathname==='/'?'/index.html':url.pathname));
  if(!file.startsWith(dist+sep)||!existsSync(file)){res.writeHead(404);return res.end();}
  res.writeHead(200,{'Content-Type':file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':'text/html'});
  res.end(readFileSync(file));
 }catch(e){errors.push(String(e));res.writeHead(500);res.end('{}');}
});
await new Promise(ok=>server.listen(0,'127.0.0.1',ok));
async function wait(fn,label){const end=Date.now()+20000;while(Date.now()<end){if(await fn())return;await sleep(100);}throw new Error(label);}
function rpc(method,params={}){return new Promise((ok,no)=>{const id=++seq,t=setTimeout(()=>no(new Error(method+' timeout')),15000);rpcPending.set(id,{ok:v=>{clearTimeout(t);ok(v);},no});ws.send(JSON.stringify({id,method,params}));});}
async function evaluate(expression){const r=await rpc('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(JSON.stringify(r.exceptionDetails));return r.result.value;}
try{
 child=spawn(binary,['--headless=new','--disable-gpu','--no-first-run','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{windowsHide:true,stdio:'ignore'});
 await wait(()=>existsSync(resolve(profile,'DevToolsActivePort')),'browser');
 const port=readFileSync(resolve(profile,'DevToolsActivePort'),'utf8').split('\n')[0];
 const pages=await(await fetch(`http://127.0.0.1:${port}/json/list`)).json();
 ws=new WebSocket(pages.find(p=>p.type==='page').webSocketDebuggerUrl);
 await new Promise(ok=>ws.addEventListener('open',ok,{once:true}));
 ws.addEventListener('message',e=>{const r=JSON.parse(e.data);if(r.id){const p=rpcPending.get(r.id);if(p){rpcPending.delete(r.id);r.error?p.no(new Error(JSON.stringify(r.error))):p.ok(r.result);}}});
 await rpc('Page.enable');await rpc('Runtime.enable');
 await rpc('Page.addScriptToEvaluateOnNewDocument',{source:`window.WebSocket=class{constructor(){this.timer=setInterval(()=>this.onmessage?.({data:JSON.stringify({source:'backend',connected:true,receivedAt:new Date().toISOString(),tasks:{items:[],totalCount:0},status:{agvs:[],safety:{dispatchingEnabled:true}},devices:{equipment:[]},resources:[],alarms:[]})}),100)}close(){clearInterval(this.timer)}};`});
 await rpc('Page.navigate',{url:`http://127.0.0.1:${server.address().port}/#/engineering-tests`});
 await wait(()=>evaluate(`document.querySelectorAll('.two-car-steps li').length===4`),'test center steps');
 const text=await evaluate(`document.body.textContent`);
 assert.ok(text.includes('六层调度现场测试'));
 assert.ok(text.includes('双车四工位六层验证'));
 assert.ok(text.includes('AGV-03保持停用'));
 assert.ok(text.includes('PLC人工联调台'));
 assert.ok(!text.includes('原运行 9740940813614e6ba584fce6d27ca909'));
 assert.equal(await evaluate(`document.querySelectorAll('.fleet-panel').length`),0);
 assert.equal(await evaluate(`document.querySelectorAll('.test-tool-grid button').length`),3);
 assert.deepEqual(errors,[]);
 console.log('PASS test center uses six-layer and PLC-lab instructions; legacy fleet run panel removed.');
}finally{
 ws?.close();child?.kill();server.closeAllConnections();await new Promise(ok=>server.close(ok));
}
