import { createServer } from 'node:http';
import { randomBytes, timingSafeEqual, createHash } from 'node:crypto';
import { mkdir, writeFile, unlink } from 'node:fs/promises';
import { join, isAbsolute } from 'node:path';
import { homedir } from 'node:os';
import { execFileSync } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';
import { interactions } from './interactions.mjs';
import { workspaces } from './workspaces.mjs';

const textOf = content => typeof content === 'string' ? content : (content ?? []).filter(p => p.type === 'text').map(p => p.text ?? '').join('\n');
const cleanId = value => {
  if (typeof value !== 'string' || !value || value.length > 512) throw new Error('Invalid session identity');
  return value;
};
const rejectSubmission = message => { throw Object.assign(new Error(message), { code: 'bridge/not-submitted' }); };
const userText = message => {
  const text = textOf(message.content);
  if (message.source?.kind !== 'user-question-reply') return text;
  try {
    const reply = JSON.parse(text);
    if (reply.kind !== 'answer_to_pending_question' || !Array.isArray(reply.questions) || !Array.isArray(reply.answers)) return text;
    return reply.questions.map(question => {
      const answer = reply.answers.find(a => a.id === question.id);
      return `${question.question}\n回答：${[...(answer?.selected ?? []), ...(answer?.custom ? [answer.custom] : [])].join('、')}`;
    }).join('\n\n');
  } catch { return text; }
};

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
  const changeListeners = new Map();
  const changed = id => {
    streamRevisions.set(id, (streamRevisions.get(id) ?? 0) + 1);
    for (const wake of changeListeners.get(id) ?? []) wake();
  };
  const disposers = [];
  const interactive = interactions(ctx, changed, config);
  const workspaceApi = workspaces(ctx);
  const fileWorkspaces = new Map();
  const systemPrompt = ctx.get?.('systemPrompt');
  const canFiles = typeof systemPrompt?.context === 'function';
  if (canFiles) disposers.push(systemPrompt.context({
    name: 'furtheragent:file-workspace', interpolate: false,
    order: (systemPrompt.getContextOrder?.('WORKING_DIRECTORY') ?? 0) + 1,
    text: context => {
      const outbox = fileWorkspaces.get(context.agent?.session.id);
      return outbox ? `FurtherAgent 文件工作区\n请将要交付给远端用户的文件保存为此目录顶层的普通文件，每个文件不超过 100 MiB。\n输出目录：${outbox}` : '';
    },
  }));
  const catalog = async () => {
    const result = await ctx.sessionController.modelCatalog();
    return { ...result, groups: result.groups.map(group => ({ ...group, models: group.models.map(model => ({ ...model,
      label: model.name ?? model.id, reasoningEfforts: model.reasoning?.efforts?.map(e => e.id) ?? [],
      defaultReasoningEffort: model.reasoning?.defaultEffort ?? null })) })) };
  };
  const permissionOptions = () => (ctx.get?.('permissionPresets')?.catalog().options ?? [])
    .filter(option => option.value !== 'custom').map(option => ({ value: option.value, label: option.name, description: option.description }));
  if (ctx.on) {
    disposers.push(ctx.on('session/event', (session, event) => {
      if (event.type === 'assistant/message' || event.type === 'turn/end') streams.delete(session.id);
    }, { global: true }));
    disposers.push(ctx.on('agent/assistant-stream', ({ agent, frame }) => {
      const id = agent.session.id;
      changed(id);
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
      const messages = []; const byId = new Map(); const acceptedRequestIds = []; let turnId = null; let lastModel; let lastEnd;
      for (const event of observation.events) {
        if (event.type === 'turn/start') { turnId = String(event.seq); lastEnd = undefined; }
        if (event.type === 'turn/end') lastEnd = event.data?.reason;
        if (event.type === 'model/selection') lastModel = event.data;
        if (event.type === 'request/header') lastModel = event.data?.header?.config ?? lastModel;
        if (event.type === 'user/message' && event.data?.source?.kind === 'user' && event.data.source.rpcId) acceptedRequestIds.push(event.data.source.rpcId);
        // Replacement copies belong to model context, not the human transcript.
        if (event.surfaceOp !== undefined && event.surfaceOp !== 'append') continue;
        if (event.type === 'user/message' && event.data?.source && !['user', 'user-question-reply'].includes(event.data.source.kind)) continue;
        if (event.type === 'tool/call') {
          messages.push({ role: 'tool.call', text: event.data?.name ?? event.data?.call?.name ?? '工具调用', detail: JSON.stringify(event.data) });
          continue;
        }
        const message = event.type === 'user/message' ? event.data : event.data?.message;
        if (!['user/message', 'assistant/message', 'tool/result'].includes(event.type) || !message) continue;
        const role = event.type === 'user/message' ? 'user' : event.type === 'tool/result' ? 'tool' : 'assistant';
        const entry = { role, text: role === 'user' ? userText(message) : textOf(message.content), ...(role === 'tool' ? { detail: JSON.stringify(message.content) } : {}) };
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
      const model = values.modelSelection?.next ?? values.modelSelection?.pending ?? values.modelSelection?.lastUsed ?? lastModel;
      const status = running ? 'active' : pending > 0 ? 'queued' : lastEnd?.kind === 'completed' || turnId === null ? 'idle'
        : ['error', 'blocked', 'max-tokens'].includes(lastEnd?.kind) ? 'failed' : 'interrupted';
      // Wire pending is a transition flag, not the requested boolean state.
      const plan = values.plan;
      // Keep a contiguous newest window within the existing 20 MiB desktop budget.
      // Whole-message eviction is preferable to breaking JSON or a UTF-8 sequence.
      const budget = 20 * 1024 * 1024;
      let size = 2; let start = messages.length;
      while (start > 0) {
        const bytes = Buffer.byteLength(JSON.stringify(messages[start - 1]), 'utf8') + 1;
        if (size + bytes > budget) {
          if (start === messages.length) {
            // An individual generated/tool message can itself exceed the window.
            const entry = messages[start - 1];
            let text = entry.text ?? '';
            while (Buffer.byteLength(JSON.stringify({ role: entry.role, text }), 'utf8') > budget - 1024) {
              let cut = Math.ceil(text.length / 8);
              if (text.charCodeAt(cut) >= 0xdc00 && text.charCodeAt(cut) <= 0xdfff) cut++;
              text = text.slice(cut);
            }
            messages[start - 1] = { role: entry.role, text: '（此条内容过长，已截取末尾）\n' + text };
            start--;
          }
          break;
        }
        size += bytes; start--;
      }
      return { id, title: (typeof values.title === 'string' ? values.title : values.title?.title) || observation.header.title || id, cwd: observation.header.cwd ?? '', status,
        turnId, messages: messages.slice(start), prompts: interactive.prompts(id, values), canSend: true, canStop: running,
        model: model?.model ?? null, provider: model?.provider ?? null, reasoningEffort: model?.reasoningEffort ?? null,
        permission: values.permissions?.currentValue ?? null, canChangePermissions: !!ctx.get?.('permissionPresets') && (agent ? !!ctx.get?.('commands')?.find(agent, 'permission') : !!values.permissions),
        canChangeMode: agent ? !!ctx.get?.('commands')?.find(agent, 'plan') : !!values.plan,
        mode: (plan?.pending ? !plan.active : plan?.active) ? 'plan' : 'default', pendingMessages: pending, acceptedRequestIds, streamRevision: streamRevisions.get(id) ?? 0,
        statusDetail: `DSH ${mode} · ${running ? '执行中' : status === 'queued' ? '排队中' : status === 'idle' ? '空闲' : status === 'failed' ? '执行失败' : '已中断'} · 待处理 ${pending} 条${plan?.pending ? ' · 模式等待下一步骤生效' : ''} · 同一 Host，会话只读订阅`, cursor: observation.cursor };
    } finally { observation[Symbol.dispose](); }
  }
  const methods = {
    info: async () => ({ protocol: 1, mode, pid: process.pid, hostVersion: config.hostVersion ?? 'unknown', bridgeVersion: config.bridgeVersion ?? 'unknown',
      activeTasks: hasActiveTasks(), instanceId: config.instanceId,
      capabilities: ['list', 'snapshot', 'watch', 'create', 'send', 'steer', 'stop', 'model', 'plan', 'delivery', 'interrupt-send', 'reasoning',
        ...(interactive.available ? ['respond'] : []), ...(canFiles ? ['files', 'thumbnails'] : []),
        ...(ctx.get?.('permissionPresets') ? ['permissions'] : []),
        ...(ctx.get?.('workspaceController') ? ['archive'] : []),
        ...(ctx.get?.('workspaceController') && ctx.get?.('workspaceRegistry') ? ['workspaces'] : [])], detail: 'Same-Host bridge; snapshots never resume an Agent.' }),
    list: async (_, signal) => {
      const result = await ctx.sessionController.list({}, signal);
      const archived = new Set(ctx.get?.('workspaceRegistry')?.archivedSessionIds ?? []);
      return { ...result, items: result.items.filter(item => !archived.has(item.sessionId)) };
    },
    snapshot: (args, signal) => snapshot(args.id, signal),
    observe: args => { cleanId(args.id); return interactive.observe(args); },
    respond: async (args, signal) => {
      cleanId(args.id); cleanId(args.requestId);
      const observation = await ctx.sessionQuery.observeSession(args.id, { signal, projectionMode: 'all' });
      try { return await interactive.respond(args, observation.projections?.values ?? {}); }
      finally { observation[Symbol.dispose](); }
    },
    workspaces: () => workspaceApi.list(),
    browseDirectories: (args, signal) => workspaceApi.browse(args.path, signal),
    addWorkspace: (args, signal) => workspaceApi.add(args, signal),
    fileWorkspace: async args => {
      cleanId(args.id);
      if (!canFiles || typeof args.outbox !== 'string' || !isAbsolute(args.outbox)) throw new Error('Native file context is unavailable or path is invalid');
      if (!fileWorkspaces.has(args.id) && fileWorkspaces.size >= 128) throw new Error('Too many file workspaces');
      await mkdir(args.outbox, { recursive: true });
      fileWorkspaces.set(args.id, args.outbox);
      return { accepted: true };
    },
    watch: async (args, signal) => {
      cleanId(args.id);
      signal?.throwIfAborted();
      if (args.observerId && typeof args.observed === 'boolean') interactive.observe(args);
      let wake;
      const changed = new Promise(resolve => { wake = resolve; });
      const wakes = changeListeners.get(args.id) ?? new Set(); wakes.add(wake); changeListeners.set(args.id, wakes);
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
      } finally {
        clearTimeout(timer); signal?.removeEventListener('abort', wake); for (const release of releases) release();
        wakes.delete(wake); if (!wakes.size) changeListeners.delete(args.id);
      }
    },
    create: async (args, signal) => {
      const cwd = args.cwd ? await workspaceApi.directory(args.cwd) : config.cwd || undefined;
      const created = await ctx.sessionController.create({ sessionId: cleanId(args.id), cwd }, signal);
      if (typeof args.title === 'string' && args.title.trim()) await ctx.sessionController.rename({ sessionId: created.sessionId, title: args.title });
      return created;
    },
    send: (args, signal) => {
      if (!['queue', 'steer'].includes(args.mode)) rejectSubmission('Unsupported delivery mode');
      if (typeof args.text !== 'string' || !args.text.trim() || args.text.length > 100000) rejectSubmission('Invalid prompt');
      return ctx.sessionController.prompt({ sessionId: cleanId(args.id), requestId: cleanId(args.requestId),
        mode: args.mode === 'steer' ? 'steer' : 'followup', content: [{ type: 'text', text: args.text }] }, signal);
    },
    interruptSend: async (args, signal) => {
      const id = cleanId(args.id);
      cleanId(args.requestId);
      if (typeof args.text !== 'string' || !args.text.trim() || args.text.length > 100000) rejectSubmission('Invalid prompt');
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
    models: () => catalog(),
    settings: async (args, signal) => ({ catalog: await catalog(), view: await snapshot(args.id, signal), permissionOptions: permissionOptions() }),
    configurePermissions: async (args, signal) => {
      const id = cleanId(args.id);
      if (!permissionOptions().some(option => option.value === args.permission)) throw new Error('Unsupported DSH permission preset');
      const resolved = await ctx.sessionController.resolveAgent(id);
      if (resolved.error) throw resolved.error;
      const commands = ctx.get?.('commands');
      if (!commands?.find(resolved.agent, 'permission')) throw new Error('This DSH preset has no permission command');
      const execution = await commands.execute(resolved.agent, '/permission ' + args.permission, [], signal);
      if (!execution || execution.result.kind === 'error') throw new Error(execution?.result?.text ?? 'Permission command failed');
      return { accepted: true };
    },
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
  return { snapshot, close() { interactive.close(); for (const dispose of disposers) dispose?.(); streams.clear(); streamRevisions.clear(); fileWorkspaces.clear(); changeListeners.clear(); }, async call(method, args, signal) {
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
