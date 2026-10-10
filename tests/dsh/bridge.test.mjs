import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { service, startBridge } from '../../plugins/furtheragent-dsh/bridge.mjs';

function fixture() {
 const calls=[]; const events=[{seq:0,type:'turn/start',data:{}},{seq:1,type:'user/message',data:{id:'u',content:[{type:'text',text:'hello'}]}},{seq:2,type:'assistant/message',data:{message:{id:'a',content:[{type:'text',text:'reply'}]}}}];
 const ctx={sessionQuery:{async observeSession(id) {calls.push('observe');return {header:{id,title:'Current desktop',cwd:'C:/test'},events,cursor:2,projections:{values:{}},[Symbol.dispose](){calls.push('dispose')}};}},agents:{get(){return {status:'running'}}},sessionController:{list:async()=>({items:[]}),prompt:async arg=>{calls.push(arg);return {accepted:true}},cancel:async()=>({accepted:true}),create:()=>{throw new Error('must not create on read')}}};
 return {ctx,calls,events};
}
test('occupied desktop history is read from the same Host without creation or resume',async()=>{
 const f=fixture();const v=await service(f.ctx).snapshot('desktop',new AbortController().signal);
 assert.equal(v.status,'active');assert.equal(v.messages.length,2);assert.deepEqual(f.calls,['observe','dispose']);
});
test('explicit queue and steer remain different; source request id is preserved',async()=>{
 const f=fixture();const api=service(f.ctx);
 for(const mode of ['queue','steer'])await api.call('send',{id:'s',text:'new',requestId:'stable-request',mode});
 assert.equal(f.calls[0].mode,'followup');assert.equal(f.calls[1].mode,'steer');assert.equal(f.calls[1].requestId,'stable-request');
 await assert.rejects(api.call('send',{id:'s',text:'new',requestId:'r',mode:'interrupt'}));
});
test('unknown methods cannot invoke arbitrary Host services',async()=>{
 await assert.rejects(service(fixture().ctx).call('constructor',{}));
 await assert.rejects(service(fixture().ctx).call('delete',{}));
});

test('human history excludes model-only replacement copies and injected user context',async()=>{
 const f=fixture();
 f.events.push({seq:3,type:'user/message',surfaceOp:'append',data:{source:{kind:'plugin'},content:[{type:'text',text:'hidden context'}]}});
 f.events.push({seq:4,type:'assistant/message',surfaceOp:{op:'replace',startSeq:1,endSeq:2},data:{message:{content:[{type:'text',text:'compacted copy'}]}}});
 assert.deepEqual((await service(f.ctx).snapshot('s')).messages.map(m=>m.text),['hello','reply']);
});

test('stream revisions accumulate, duplicated frames do not duplicate text, commit replaces draft',async()=>{
 const f=fixture(); const handlers=new Map();
 f.ctx.on=(name,fn)=>{handlers.set(name,fn);return()=>handlers.delete(name)};
 const api=service(f.ctx); const emit=frame=>handlers.get('agent/assistant-stream')({agent:{session:{id:'s'}},frame});
 emit({type:'start',attemptId:'a',revision:1});
 emit({type:'chunk',attemptId:'a',revision:2,index:0,chunk:{type:'reasoning-delta',text:'thinking'}});
 const chunk={type:'chunk',attemptId:'a',revision:3,index:1,chunk:{type:'text-delta',text:'first'}};
 emit(chunk);emit(chunk);
 emit({type:'chunk',attemptId:'a',revision:4,index:2,chunk:{type:'text-delta',text:' second'}});
 let view=await api.snapshot('s');
 assert.equal(view.messages.at(-1).text,'first second');assert.equal(view.messages.at(-2).text,'thinking');
 handlers.get('session/event')({id:'s'},{type:'assistant/message'});
 view=await api.snapshot('s');assert.equal(view.messages.length,2);
 api.close();assert.equal(handlers.size,0);
});
test('local bridge requires authentication, rejects browser origins, disposes descriptor',async()=>{
 const dir=await mkdtemp(join(tmpdir(),'fa-dsh-test-'));
 const bridge=await startBridge(fixture().ctx,{directory:dir});
 const d=JSON.parse(await readFile(bridge.descriptor,'utf8'));
 try {
  assert.equal(new URL(d.endpoint).hostname,'127.0.0.1');
  const body=JSON.stringify({method:'snapshot',args:{id:'desktop'}});
  assert.equal((await fetch(d.endpoint+'/rpc',{method:'POST',body})).status,401);
  assert.equal((await fetch(d.endpoint+'/rpc',{method:'POST',body,headers:{Authorization:'Bearer '+d.token,Origin:'http://evil'}})).status,401);
  const r=await fetch(d.endpoint+'/rpc',{method:'POST',body,headers:{Authorization:'Bearer '+d.token}});
  assert.equal(r.status,200);assert.equal((await r.json()).result.id,'desktop');
 } finally {await bridge.close();}
 await assert.rejects(readFile(bridge.descriptor));await rm(dir,{recursive:true});
});
