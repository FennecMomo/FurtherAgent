import { createServer } from 'node:http';
import { randomBytes, timingSafeEqual } from 'node:crypto';
import { mkdir, writeFile, unlink } from 'node:fs/promises';
import { join } from 'node:path';
import { homedir } from 'node:os';
import { execFileSync } from 'node:child_process';

const textOf = content => typeof content === 'string' ? content : (content ?? []).filter(p => p.type === 'text').map(p => p.text ?? '').join('\n');
const cleanId = value => {
  if (typeof value !== 'string' || !value || value.length > 512) throw new Error('Invalid session identity');
  return value;
};

// Borrow Host services; never construct a Context, Session or Agent for a read.
export function service(ctx, config = {}) {
  const mode = config.mode === 'background' ? 'background' : 'desktop';
  const streams = new Map();
  const disposers = [];
  if (ctx.on) {
    disposers.push(ctx.on('session/event', (session, event) => {
      if (event.type === 'assistant/message' || event.type === 'turn/end') streams.delete(session.id);
    }, { global: true }));
    disposers.push(ctx.on('agent/assistant-stream', ({ agent, frame }) => {
      const id = agent.session.id;
      if (frame.type === 'end') { streams.delete(id); return; }
      let state = streams.get(id);
      if (!state || state.attemptId !== frame.attemptId || frame.type === 'start') {
        state = { attemptId: frame.attemptId, revision: -1, index: -1, text: '', reasoning: '' };
        if (streams.size >= 64) streams.delete(streams.keys().next().value);
        streams.set(id, state);
      }
      if (frame.revision <= state.revision) return;
      state.revision = frame.revision;
      if (frame.type !== 'chunk' || frame.index <= state.index) return;
      state.index = frame.index;
      const key = frame.chunk?.type === 'text-delta' ? 'text' : frame.chunk?.type === 'reasoning-delta' ? 'reasoning' : null;
      if (key && typeof frame.chunk.text === 'string') state[key] = (state[key] + frame.chunk.text).slice(-512 * 1024);
    }, { global: true }));
  }
  async function snapshot(id, signal) {
    cleanId(id);
    const observation = await ctx.sessionQuery.observeSession(id, { signal, projectionMode: 'all' });
    try {
      const messages = []; const byId = new Map(); let turnId = null;
      for (const event of observation.events) {
        if (event.type === 'turn/start') turnId = String(event.seq);
        // Replacement copies belong to model context, not the human transcript.
        if (event.surfaceOp !== undefined && event.surfaceOp !== 'append') continue;
        if (event.type === 'user/message' && event.data?.source && event.data.source.kind !== 'user') continue;
        if (event.type === 'tool/call') {
          messages.push({ role: 'tool.call', text: event.data?.name ?? event.data?.call?.name ?? '工具调用', detail: JSON.stringify(event.data) });
          continue;
        }
        const message = event.type === 'user/message' ? event.data : event.data?.message;
        if (!['user/message', 'assistant/message', 'tool/result'].includes(event.type) || !message) continue;
        const role = event.type === 'user/message' ? 'user' : event.type === 'tool/result' ? 'tool' : 'assistant';
        const entry = { role, text: textOf(message.content), ...(role === 'tool' ? { detail: JSON.stringify(message.content) } : {}) };
        if (message.id && byId.has(message.id)) messages[byId.get(message.id)] = entry;
        else { if (message.id) byId.set(message.id, messages.length); messages.push(entry); }
        if (role === 'assistant') for (const part of message.content ?? []) {
          if (part.type === 'reasoning' && typeof part.text === 'string' && part.text.trim())
            messages.push({ role: 'reasoning.available', text: part.text });
        }
      }
      const agent = ctx.agents.get(id); const running = agent?.status === 'running';
      const pending = (agent?.inbox?.nextTurn?.length ?? 0) + (agent?.inbox?.nextStep?.length ?? 0);
      const stream = streams.get(id);
      if (running && stream?.reasoning) messages.push({ role: 'reasoning.available', text: stream.reasoning });
      if (running && stream?.text) messages.push({ role: 'assistant', text: stream.text });
      const values = observation.projections?.values ?? {};
      const model = values.modelSelection?.next ?? values.modelSelection?.lastUsed;
      return { id, title: observation.header.title || id, cwd: observation.header.cwd ?? '', status: running ? 'active' : 'idle',
        turnId, messages: messages.slice(-500), prompts: [], canSend: true, canStop: running,
        model: model?.model ?? null, mode: 'default',
        statusDetail: `DSH ${mode} · ${running ? '执行中' : '空闲'} · 待处理 ${pending} 条 · 同一 Host，会话只读订阅`, cursor: observation.cursor };
    } finally { observation[Symbol.dispose](); }
  }
  const methods = {
    info: async () => ({ protocol: 1, mode, pid: process.pid, hostVersion: config.hostVersion ?? 'unknown',
      activeTasks: (ctx.agents.list?.() ?? []).some(a => a.status === 'running' || a.inbox?.nextTurn?.length || a.inbox?.nextStep?.length),
      capabilities: ['list', 'snapshot', 'create', 'send', 'steer', 'stop', 'model'], detail: 'Same-Host bridge; snapshots never resume an Agent.' }),
    list: async (_, signal) => ctx.sessionController.list({}, signal),
    snapshot: (args, signal) => snapshot(args.id, signal),
    create: (args, signal) => ctx.sessionController.create({ sessionId: cleanId(args.id), cwd: config.cwd || undefined }, signal),
    send: (args, signal) => {
      if (!['queue', 'steer'].includes(args.mode)) throw new Error('Unsupported delivery mode');
      if (typeof args.text !== 'string' || !args.text.trim() || args.text.length > 100000) throw new Error('Invalid prompt');
      return ctx.sessionController.prompt({ sessionId: cleanId(args.id), requestId: cleanId(args.requestId),
        mode: args.mode === 'steer' ? 'steer' : 'followup', content: [{ type: 'text', text: args.text }] }, signal);
    },
    stop: args => ctx.sessionController.cancel({ sessionId: cleanId(args.id) }),
    models: () => ctx.sessionController.modelCatalog(),
    configure: args => ctx.sessionController.selectModel({ sessionId: cleanId(args.id), provider: args.provider, model: args.model,
      ...(args.reasoningEffort ? { reasoningEffort: args.reasoningEffort } : {}) }),
    shutdown: () => {
      if (mode !== 'background') throw new Error('Desktop lifecycle belongs to DSH');
      if ((ctx.agents.list?.() ?? []).some(a => a.status === 'running' || a.inbox.nextTurn.length || a.inbox.nextStep.length))
        throw new Error('DSH 后台仍有运行或排队任务，请先停止任务再退出。');
      setTimeout(() => process.emit('SIGTERM'), 100).unref();
      return { accepted: true };
    },
  };
  return { snapshot, close() { for (const dispose of disposers) dispose?.(); streams.clear(); }, async call(method, args, signal) {
    if (!Object.hasOwn(methods, method)) throw new Error('Unsupported bridge method');
    return methods[method](args ?? {}, signal);
  } };
}

export async function startBridge(ctx, config = {}) {
  const api = service(ctx, config); const token = randomBytes(32).toString('hex');
  const directory = config.directory ?? join(process.env.LOCALAPPDATA ?? join(homedir(), '.local', 'share'), 'FurtherAgent', 'dsh', 'instances');
  await mkdir(directory, { recursive: true, mode: 0o700 });
  if (process.platform === 'win32') {
    const sid = execFileSync('whoami.exe', ['/user', '/fo', 'csv', '/nh'], { encoding: 'utf8', windowsHide: true }).match(/S-1-5-[0-9-]+/)?.[0];
    if (!sid) throw new Error('Unable to determine current-user ACL');
    execFileSync('icacls.exe', [directory, '/inheritance:r', '/grant:r', `*${sid}:(OI)(CI)F`, '*S-1-5-18:(OI)(CI)F'], { windowsHide: true, stdio: 'ignore' });
  }
  const active = new Set();
  const server = createServer(async (req, res) => {
    const supplied = Buffer.from(req.headers.authorization ?? ''); const expected = Buffer.from(`Bearer ${token}`);
    if (req.headers.origin || supplied.length !== expected.length || !timingSafeEqual(supplied, expected)) { res.writeHead(401).end(); return; }
    const abort = new AbortController(); active.add(abort); res.on('close', () => abort.abort());
    try {
      if (req.method !== 'POST' || req.url !== '/rpc') { res.writeHead(404).end(); return; }
      let size = 0; const chunks = [];
      for await (const chunk of req) { size += chunk.length; if (size > 512 * 1024) throw new Error('Request too large'); chunks.push(chunk); }
      const { method, args } = JSON.parse(Buffer.concat(chunks).toString('utf8'));
      const result = await api.call(method, args, abort.signal);
      res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }).end(JSON.stringify({ result }));
    } catch (error) {
      if (!res.destroyed) res.writeHead(400, { 'Content-Type': 'application/json' }).end(JSON.stringify({ error: { code: error.code ?? 'bridge/error', message: String(error.message ?? error) } }));
    } finally { active.delete(abort); }
  });
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  const mode = config.mode === 'background' ? 'background' : 'desktop';
  const descriptor = join(directory, `${mode}-${process.pid}.json`);
  try { await writeFile(descriptor, JSON.stringify({ protocol: 1, mode, pid: process.pid, endpoint: `http://127.0.0.1:${server.address().port}`, token }), { mode: 0o600, flag: 'wx' }); }
  catch (error) { api.close(); server.close(); throw error; }
  return { descriptor, async close() { api.close(); for (const abort of active) abort.abort(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); await unlink(descriptor).catch(() => {}); } };
}
