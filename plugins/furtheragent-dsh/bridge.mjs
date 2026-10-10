import { createServer } from 'node:http';
import { randomBytes, timingSafeEqual, createHash } from 'node:crypto';
import { mkdir, writeFile, unlink } from 'node:fs/promises';
import { join } from 'node:path';
import { homedir } from 'node:os';
import { execFileSync } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';

const textOf = content => typeof content === 'string' ? content : (content ?? []).filter(p => p.type === 'text').map(p => p.text ?? '').join('\n');
const cleanId = value => {
  if (typeof value !== 'string' || !value || value.length > 512) throw new Error('Invalid session identity');
  return value;
};
const rejectSubmission = message => { throw Object.assign(new Error(message), { code: 'bridge/not-submitted' }); };

// Borrow Host services; never construct a Context, Session or Agent for a read.
export function service(ctx, config = {}) {
  const mode = config.mode === 'background' ? 'background' : 'desktop';
  const hasActiveTasks = () => {
    const agents = ctx.agents.list?.() ?? [];
    const jobs = ctx.get?.('jobs');
    return agents.some(a => a.status === 'running' || a.inbox?.nextTurn?.length || a.inbox?.nextStep?.length)
      || (jobs && [undefined, ...agents.map(a => a.id)].some(id => jobs.list(id).some(job => ['running', 'stopping'].includes(job.status)))) || false;
  };
  const streams = new Map();
  const streamRevisions = new Map();
  const disposers = [];
  if (ctx.on) {
    disposers.push(ctx.on('session/event', (session, event) => {
      if (event.type === 'assistant/message' || event.type === 'turn/end') streams.delete(session.id);
    }, { global: true }));
    disposers.push(ctx.on('agent/assistant-stream', ({ agent, frame }) => {
      const id = agent.session.id;
      if (streamRevisions.size >= 64 && !streamRevisions.has(id)) streamRevisions.delete(streamRevisions.keys().next().value);
      streamRevisions.set(id, frame.revision);
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
      const messages = []; const byId = new Map(); const acceptedRequestIds = []; let turnId = null; let lastModel;
      for (const event of observation.events) {
        if (event.type === 'turn/start') turnId = String(event.seq);
        if (event.type === 'model/selection') lastModel = event.data;
        if (event.type === 'request/header') lastModel = event.data?.header?.config ?? lastModel;
        if (event.type === 'user/message' && event.data?.source?.kind === 'user' && event.data.source.rpcId) acceptedRequestIds.push(event.data.source.rpcId);
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
      for (const message of [...(agent?.inbox?.nextTurn ?? []), ...(agent?.inbox?.nextStep ?? [])])
        if (message.source?.kind === 'user' && message.source.rpcId) acceptedRequestIds.push(message.source.rpcId);
      const stream = streams.get(id);
      if (running && stream?.reasoning) messages.push({ role: 'reasoning.available', text: stream.reasoning });
      if (running && stream?.text) messages.push({ role: 'assistant', text: stream.text });
      const values = observation.projections?.values ?? {};
      const model = values.modelSelection?.pending ?? lastModel;
      // Wire pending is a transition flag, not the requested boolean state.
      const plan = values.plan;
      return { id, title: (typeof values.title === 'string' ? values.title : values.title?.title) || observation.header.title || id, cwd: observation.header.cwd ?? '', status: running ? 'active' : 'idle',
        turnId, messages: messages.slice(-500), prompts: [], canSend: true, canStop: running,
        model: model?.model ?? null, provider: model?.provider ?? null, reasoningEffort: model?.reasoningEffort ?? null,
        canChangeMode: agent ? !!ctx.get?.('commands')?.find(agent, 'plan') : !!values.plan,
        mode: (plan?.pending ? !plan.active : plan?.active) ? 'plan' : 'default', pendingMessages: pending, acceptedRequestIds, streamRevision: streamRevisions.get(id) ?? 0,
        statusDetail: `DSH ${mode} · ${running ? '执行中' : '空闲'} · 待处理 ${pending} 条${plan?.pending ? ' · 模式等待下一步骤生效' : ''} · 同一 Host，会话只读订阅`, cursor: observation.cursor };
    } finally { observation[Symbol.dispose](); }
  }
  const methods = {
    info: async () => ({ protocol: 1, mode, pid: process.pid, hostVersion: config.hostVersion ?? 'unknown',
      activeTasks: hasActiveTasks(), instanceId: config.instanceId,
      capabilities: ['list', 'snapshot', 'watch', 'create', 'send', 'steer', 'stop', 'model', 'plan', 'delivery', 'interrupt-send', ...(ctx.get?.('workspaceController') ? ['archive'] : [])], detail: 'Same-Host bridge; snapshots never resume an Agent.' }),
    list: async (_, signal) => {
      const result = await ctx.sessionController.list({}, signal);
      const archived = new Set(ctx.get?.('workspaceRegistry')?.archivedSessionIds ?? []);
      return { ...result, items: result.items.filter(item => !archived.has(item.sessionId)) };
    },
    snapshot: (args, signal) => snapshot(args.id, signal),
    watch: async (args, signal) => {
      cleanId(args.id);
      signal?.throwIfAborted();
      let wake;
      const changed = new Promise(resolve => { wake = resolve; });
      const releases = [
        ctx.on('session/event', session => { if (session.id === args.id) wake(); }, { global: true }),
        ctx.on('agent/assistant-stream', ({ agent }) => { if (agent.session.id === args.id) wake(); }, { global: true }),
      ];
      const timer = setTimeout(wake, 15000); timer.unref();
      signal?.addEventListener('abort', wake, { once: true });
      try {
        // Subscribe before reading so an event during the snapshot cannot be lost.
        const initial = await snapshot(args.id, signal);
        if (initial.cursor !== args.cursor || initial.streamRevision !== args.streamRevision || initial.status !== args.status || initial.pendingMessages !== args.pendingMessages) return initial;
        await changed;
        signal?.throwIfAborted();
        await delay(50, undefined, { signal });
        return await snapshot(args.id, signal);
      } finally { clearTimeout(timer); signal?.removeEventListener('abort', wake); for (const release of releases) release(); }
    },
    create: async (args, signal) => {
      const created = await ctx.sessionController.create({ sessionId: cleanId(args.id), cwd: config.cwd || undefined }, signal);
      if (typeof args.title === 'string' && args.title.trim()) await ctx.sessionController.rename({ sessionId: created.sessionId, title: args.title });
      return created;
    },
    send: (args, signal) => {
      if (!['queue', 'steer'].includes(args.mode)) throw new Error('Unsupported delivery mode');
      if (typeof args.text !== 'string' || !args.text.trim() || args.text.length > 100000) throw new Error('Invalid prompt');
      return ctx.sessionController.prompt({ sessionId: cleanId(args.id), requestId: cleanId(args.requestId),
        mode: args.mode === 'steer' ? 'steer' : 'followup', content: [{ type: 'text', text: args.text }] }, signal);
    },
    interruptSend: async (args, signal) => {
      const id = cleanId(args.id);
      cleanId(args.requestId);
      if (typeof args.text !== 'string' || !args.text.trim() || args.text.length > 100000) throw new Error('Invalid prompt');
      const agent = ctx.agents.get(id);
      if (!agent) rejectSubmission('Session is not active in this Host');
      const messages = [...(agent.inbox?.nextTurn ?? []), ...(agent.inbox?.nextStep ?? [])];
      const accepted = message => message?.source?.kind === 'user' && message.source.rpcId === args.requestId;
      if (messages.some(accepted) || agent.session.snapshotEvents().some(e => e.type === 'user/message' && accepted(e.data))) return { accepted: true };
      const validate = () => {
        const turn = agent.session.snapshotEvents().findLast(e => e.type === 'turn/start');
        if (String(turn?.seq) !== args.turnId) rejectSubmission('运行已变化，未发送新消息');
        if (agent.inbox.nextTurn.length || agent.inbox.nextStep.length) rejectSubmission('DSH 尚有待处理队列，未清空队列，也未提交新消息；请先处理队列。');
      };
      validate();
      if (agent.status === 'running') await ctx.sessionController.cancel({ sessionId: id });
      const until = Date.now() + 25000;
      while (agent.status === 'running') {
        if (Date.now() >= until) rejectSubmission('停止确认超时，新消息未发送');
        await delay(100, undefined, { signal });
        validate();
      }
      validate();
      signal?.throwIfAborted();
      return methods.send({ ...args, mode: 'queue' }, signal);
    },
    stop: args => ctx.sessionController.cancel({ sessionId: cleanId(args.id) }),
    models: () => ctx.sessionController.modelCatalog(),
    settings: async (args, signal) => ({ catalog: await ctx.sessionController.modelCatalog(), view: await snapshot(args.id, signal) }),
    configure: async (args, signal) => {
      const id = cleanId(args.id);
      if (args.mode !== undefined && !['default', 'build', 'plan'].includes(args.mode)) throw new Error('Unsupported DSH mode');
      const resolved = await ctx.sessionController.resolveAgent(id);
      if (resolved.error) throw resolved.error;
      const agent = resolved.agent;
      const commands = ctx.get?.('commands');
      if (args.mode !== undefined && !commands?.find(agent, 'plan')) throw new Error('This DSH preset has no Plan command');
      const before = await snapshot(id, signal);
      const selection = before.model ? before : (await ctx.sessionController.modelCatalog()).default;
      const sameModel = selection?.model === args.model && selection?.provider === args.provider;
      const result = sameModel && !args.reasoningEffort ? { selected: { provider: selection.provider, model: selection.model } }
        : await ctx.sessionController.selectModel({ sessionId: id, provider: args.provider, model: args.model,
          ...(args.reasoningEffort ? { reasoningEffort: args.reasoningEffort } : {}) });
      if (args.mode !== undefined) {
        const execution = await commands.execute(agent, args.mode === 'plan' ? '/plan' : '/plan off', [], signal);
        if (!execution || execution.result.kind === 'error') throw new Error(execution?.result?.text ?? 'Plan command unavailable');
        result.plan = execution.result.text;
      }
      return result;
    },
    archive: async args => {
      if (typeof args.archived !== 'boolean') throw new Error('Archived must be boolean');
      const controller = ctx.get?.('workspaceController');
      if (!controller) throw new Error('Native archive is unavailable');
      const id = cleanId(args.id);
      const result = await controller[args.archived ? 'archiveSession' : 'unarchiveSession']({ sessionId: id });
      if (result.archivedSessionIds.includes(id) !== args.archived) throw new Error('Archive readback mismatch');
      return { archived: args.archived };
    },
    shutdown: () => {
      if (mode !== 'background') throw new Error('Desktop lifecycle belongs to DSH');
      if (hasActiveTasks())
        throw new Error('DSH 后台仍有运行或排队任务，请先停止任务再退出。');
      setTimeout(() => process.emit('SIGTERM'), 100).unref();
      return { accepted: true };
    },
  };
  return { snapshot, close() { for (const dispose of disposers) dispose?.(); streams.clear(); streamRevisions.clear(); }, async call(method, args, signal) {
    if (!Object.hasOwn(methods, method)) throw new Error('Unsupported bridge method');
    return methods[method](args ?? {}, signal);
  } };
}

export async function startBridge(ctx, config = {}) {
  const home = ctx.get?.('profileContext')?.home ?? process.env.DSH_HOME ?? join(homedir(), '.dsh');
  const instanceId = createHash('sha256').update(`${home}\n${config.mode === 'background' ? 'background' : 'desktop'}`).digest('hex');
  config = { ...config, instanceId };
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
  try { await writeFile(descriptor, JSON.stringify({ protocol: 1, mode, pid: process.pid, instanceId, endpoint: `http://127.0.0.1:${server.address().port}`, token }), { mode: 0o600, flag: 'wx' }); }
  catch (error) { api.close(); server.close(); throw error; }
  return { descriptor, async close() { api.close(); for (const abort of active) abort.abort(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); await unlink(descriptor).catch(() => {}); } };
}
