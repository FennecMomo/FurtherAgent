import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, rm, stat, writeFile } from 'node:fs/promises';
import { join, resolve, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { service } from '../../plugins/furtheragent-dsh/bridge.mjs';

const budget = 20 * 1024 * 1024;
const message = (seq, text, role = 'assistant') => ({ seq, type: `${role}/message`,
  data: role === 'user' ? { id: `m${seq}`, source: { kind: 'user' }, content: [{ type: 'text', text }] }
    : { message: { id: `m${seq}`, content: [{ type: 'text', text }] } } });

// These are borrowed native services, never a real DSH profile or process.
function fixture(t, { native = true, events = [], values = {}, config = {} } = {}) {
  const calls = [];
  const contexts = [];
  const listeners = new Map();
  const agent = { id: 's', status: 'idle', inbox: { nextTurn: [], nextStep: [] }, session: { id: 's', snapshotEvents: () => events } };
  const registry = { archivedSessionIds: [], entries: [], list() { calls.push(['workspace.list']); return this.entries; } };
  const commands = { allowed: new Set(['permission', 'plan']), find: (_, name) => commands.allowed.has(name),
    async execute(target, text, args) { calls.push(['command', target.id, text, args]); return { result: { kind: 'text', text: 'native accepted' } }; } };
  const services = native ? {
    workspaceRegistry: registry,
    workspaceController: { async create(args) {
      calls.push(['workspace.create', args]);
      const workspace = { id: 'native-workspace-id', title: 'Native workspace', path: args.path };
      registry.entries.push(workspace); return { workspace };
    } },
    permissionPresets: { catalog: () => ({ options: [
      { value: 'workspace-write', name: 'Workspace', description: 'Native workspace preset' },
      { value: 'danger-full-access', name: 'Full access', description: 'Native unrestricted preset' },
      { value: 'custom', name: 'Custom' },
    ] }) },
    commands,
    systemPrompt: { getContextOrder: name => name === 'WORKING_DIRECTORY' ? 30 : 0,
      context(value) { contexts.push(value); calls.push(['context.register', value.name]); return () => calls.push(['context.dispose', value.name]); } },
  } : {};
  const ctx = {
    get: name => services[name],
    agents: { get: id => id === agent.session.id ? agent : undefined, list: () => [agent], ...(native ? { roots: () => [agent] } : {}) },
    sessionQuery: { async observeSession(id) {
      calls.push(['observe', id]);
      return { header: { id, title: 'Native session', cwd: 'native-cwd' }, events,
        cursor: events.length, projections: { values }, [Symbol.dispose]() { calls.push(['observation.dispose', id]); } };
    } },
    sessionController: {
      async list() { calls.push(['session.list']); return { items: [] }; },
      async create(args) { calls.push(['session.create', args]); return { sessionId: args.sessionId, cwd: args.cwd }; },
      async rename(args) { calls.push(['session.rename', args]); return { renamed: true }; },
      async resolveAgent(id) { calls.push(['resolve', id]); return { agent }; },
      async prompt(args) { calls.push(['prompt', args]); return { accepted: true }; },
      async modelCatalog() { calls.push(['models']); return { default: { provider: 'provider-a', model: 'model-a' }, groups: [
        { id: 'provider-a', models: [{ id: 'model-a', name: 'Model A', reasoning: { efforts: [{ id: 'none' }, { id: 'high' }], defaultEffort: 'high' } }] },
      ] }; },
      async selectModel(args) { calls.push(['selectModel', args]); values.modelSelection = { next: args }; return { selected: args }; },
    },
  };
  if (native) ctx.on = (name, fn) => {
    if (!listeners.has(name)) listeners.set(name, new Set()); listeners.get(name).add(fn);
    return () => listeners.get(name).delete(fn);
  };
  const api = service(ctx, config);
  let closed = false;
  const close = () => { if (!closed) { closed = true; api.close(); } };
  t.after(close);
  return { api, close, ctx, services, registry, commands, calls, contexts, agent, values, events, listeners };
}

async function temporaryDirectory(t) {
  const path = await mkdtemp(join(tmpdir(), 'furtheragent-dsh-features-'));
  t.after(() => rm(path, { recursive: true, force: true }));
  return path;
}

test('optional bridge capabilities follow the available native services', async t => {
  const basic = fixture(t, { native: false });
  const complete = fixture(t);
  const basicInfo = await basic.api.call('info');
  const completeInfo = await complete.api.call('info');
  for (const name of ['files', 'thumbnails', 'workspaces', 'permissions', 'archive', 'respond']) {
    assert.ok(!basicInfo.capabilities.includes(name), `${name} must not be claimed by a basic Host`);
    assert.ok(completeInfo.capabilities.includes(name), `${name} is advertised with its native service`);
  }
  const root = await temporaryDirectory(t);
  await assert.rejects(basic.api.call('fileWorkspace', { id: 's', outbox: root }));
  assert.equal(basic.calls.filter(([kind]) => kind === 'prompt').length, 0);
});

test('native workspaces preserve identities and paths; directory browsing excludes files', async t => {
  const f = fixture(t);
  const root = await temporaryDirectory(t);
  await mkdir(join(root, 'B'));
  await mkdir(join(root, 'A'));
  await writeFile(join(root, 'not-a-directory.txt'), 'fixture');
  f.registry.entries.push({ workspaceId: 'existing-native-id', title: 'Existing', path: root });
  assert.deepEqual(await f.api.call('workspaces'), [{ id: 'existing-native-id', name: 'Existing', path: root }]);
  const browse = await f.api.call('browseDirectories', { path: root });
  assert.equal(browse.path, resolve(root));
  assert.equal(browse.parent, dirname(root));
  assert.deepEqual(browse.directories, [{ name: 'A', path: join(root, 'A') }, { name: 'B', path: join(root, 'B') }]);
  const created = await f.api.call('addWorkspace', { id: 'caller-request-id', path: root, folder: 'New workspace' });
  assert.deepEqual(created, { id: 'native-workspace-id', name: 'Native workspace', path: join(root, 'New workspace') });
  assert.ok((await stat(created.path)).isDirectory());
  assert.deepEqual(f.calls.filter(([kind]) => kind === 'workspace.create'), [['workspace.create', { path: created.path }]]);
  assert.equal(f.calls.filter(([kind]) => kind === 'session.create' || kind === 'resolve').length, 0);
});

test('workspace mutations reject relative paths, traversal and existing folders before native creation', async t => {
  const f = fixture(t);
  const root = await temporaryDirectory(t);
  await mkdir(join(root, 'existing'));
  for (const folder of ['..', '.', '../outside', 'nested/path', 'nested\\path', 'existing'])
    await assert.rejects(f.api.call('addWorkspace', { path: root, folder }), `reject folder ${folder}`);
  await assert.rejects(f.api.call('addWorkspace', { path: 'relative-path' }));
  await writeFile(join(root, 'file'), 'not a directory');
  await assert.rejects(f.api.call('addWorkspace', { path: join(root, 'file') }));
  assert.equal(f.calls.filter(([kind]) => kind === 'workspace.create').length, 0);
});

test('new sessions use the validated requested cwd and native rename without resuming a session', async t => {
  const f = fixture(t);
  const root = await temporaryDirectory(t);
  assert.deepEqual(await f.api.call('create', { id: 'new-session', cwd: root, title: 'Phone task' }), { sessionId: 'new-session', cwd: root });
  assert.deepEqual(f.calls.filter(([kind]) => kind.startsWith('session.')), [
    ['session.create', { sessionId: 'new-session', cwd: root }],
    ['session.rename', { sessionId: 'new-session', title: 'Phone task' }],
  ]);
  await assert.rejects(f.api.call('create', { id: 'bad-session', cwd: 'relative-cwd' }));
  await assert.rejects(f.api.call('create', { id: 'bad-session', cwd: join(root, 'missing') }));
  assert.equal(f.calls.filter(([kind]) => kind === 'session.create').length, 1);
  assert.equal(f.calls.filter(([kind]) => kind === 'resolve').length, 0);
  const configured = fixture(t, { config: { cwd: root } });
  assert.equal((await configured.api.call('create', { id: 'default-cwd' })).cwd, root);
});

test('model catalog exposes native reasoning efforts and settings expose actual permission presets', async t => {
  const f = fixture(t, { values: { modelSelection: { lastUsed: { provider: 'provider-a', model: 'model-a', reasoningEffort: 'high' } }, permissions: { currentValue: 'custom' } } });
  const settings = await f.api.call('settings', { id: 's' });
  assert.deepEqual(settings.catalog.groups[0].models[0].reasoningEfforts, ['none', 'high']);
  assert.equal(settings.catalog.groups[0].models[0].defaultReasoningEffort, 'high');
  assert.equal(settings.catalog.groups[0].models[0].label, 'Model A');
  assert.equal(settings.view.reasoningEffort, 'high');
  assert.equal(settings.view.permission, 'custom');
  assert.deepEqual(settings.permissionOptions, [
    { value: 'workspace-write', label: 'Workspace', description: 'Native workspace preset' },
    { value: 'danger-full-access', label: 'Full access', description: 'Native unrestricted preset' },
  ]);
  assert.ok(settings.view.canChangePermissions);
  assert.equal(f.calls.filter(([kind]) => kind === 'resolve' || kind === 'selectModel' || kind === 'command').length, 0);
});

test('reasoning changes reach native model selection even when the model is unchanged', async t => {
  const f = fixture(t, { values: { modelSelection: { lastUsed: { provider: 'provider-a', model: 'model-a', reasoningEffort: 'none' } } } });
  await f.api.call('configure', { id: 's', provider: 'provider-a', model: 'model-a', mode: 'plan', reasoningEffort: 'high' });
  assert.deepEqual(f.calls.filter(([kind]) => kind === 'selectModel'), [['selectModel', { sessionId: 's', provider: 'provider-a', model: 'model-a', reasoningEffort: 'high' }]]);
  assert.deepEqual(f.calls.filter(([kind]) => kind === 'command'), [['command', 's', '/plan', []]]);
  await f.api.call('configure', { id: 's', provider: 'provider-a', model: 'model-a', mode: 'default' });
  assert.equal(f.calls.filter(([kind]) => kind === 'selectModel').length, 1, 'an unchanged model does not reset its native reasoning selection');
  assert.equal(f.calls.filter(([kind]) => kind === 'command').at(-1)[2], '/plan off');
  await assert.rejects(f.api.call('configure', { id: 's', provider: 'provider-a', model: 'model-a', mode: 'unknown' }));
});

test('permission changes use native values and reject custom or Codex-only profile names', async t => {
  const f = fixture(t);
  for (const permission of ['custom', ':workspace', ':danger-full-access', 'unknown'])
    await assert.rejects(f.api.call('configurePermissions', { id: 's', permission }));
  assert.equal(f.calls.filter(([kind]) => kind === 'resolve' || kind === 'command').length, 0);
  await f.api.call('configurePermissions', { id: 's', permission: 'workspace-write' });
  assert.deepEqual(f.calls.filter(([kind]) => kind === 'command'), [['command', 's', '/permission workspace-write', []]]);
  f.commands.allowed.delete('permission');
  await assert.rejects(f.api.call('configurePermissions', { id: 's', permission: 'danger-full-access' }));
  assert.equal(f.calls.filter(([kind]) => kind === 'command').length, 1);
});

test('file workspace is session-scoped native system context, never an injected user message', async t => {
  const f = fixture(t, { events: [message(1, 'original request', 'user')] });
  const root = await temporaryDirectory(t);
  const outbox = join(root, 'outbox');
  assert.equal(f.contexts.length, 1);
  const context = f.contexts[0];
  assert.equal(context.interpolate, false);
  assert.equal(context.order, 31);
  assert.equal(context.text({ agent: { session: { id: 's' } } }), '');
  await f.api.call('fileWorkspace', { id: 's', outbox });
  assert.ok((await stat(outbox)).isDirectory());
  assert.ok(context.text({ agent: { session: { id: 's' } } }).includes(outbox));
  assert.equal(context.text({ agent: { session: { id: 'other-session' } } }), '');
  assert.equal(f.events.length, 1);
  assert.equal(f.calls.filter(([kind]) => kind === 'prompt').length, 0);
  await assert.rejects(f.api.call('fileWorkspace', { id: 's', outbox: 'relative-outbox' }));
  await f.api.call('send', { id: 's', requestId: 'r', mode: 'queue', text: 'next request' });
  assert.deepEqual(f.calls.find(([kind]) => kind === 'prompt')[1].content, [{ type: 'text', text: 'next request' }]);
  assert.deepEqual((await f.api.snapshot('s')).messages.map(entry => entry.text), ['original request']);
  f.close();
  assert.equal(f.calls.filter(([kind]) => kind === 'context.dispose').length, 1);
});

test('native modelSelection.next overrides lastUsed and read-only fallback preserves the last model', async t => {
  const events = [{ seq: 1, type: 'model/selection', data: { provider: 'event-provider', model: 'event-model' } }];
  const f = fixture(t, { events });
  const cases = [
    [{ next: { provider: 'next-provider', model: 'next-model', reasoningEffort: 'high' }, lastUsed: { provider: 'last-provider', model: 'last-model' } }, ['next-provider', 'next-model', 'high']],
    [{ next: null, lastUsed: { provider: 'last-provider', model: 'last-model', reasoningEffort: 'none' } }, ['last-provider', 'last-model', 'none']],
    [undefined, ['event-provider', 'event-model', null]],
  ];
  for (const [selection, expected] of cases) {
    f.values.modelSelection = selection;
    const view = await f.api.snapshot('s');
    assert.deepEqual([view.provider, view.model, view.reasoningEffort], expected);
  }
  assert.equal(f.calls.filter(([kind]) => kind === 'resolve' || kind === 'selectModel' || kind === 'session.create').length, 0);
});

test('long Unicode history retains a contiguous newest window measured as serialized UTF-8 bytes', async t => {
  const texts = Array.from({ length: 7 }, (_, i) => `[${i}]` + '鲸🐳\\\n"'.repeat(320_000));
  const f = fixture(t, { events: texts.map((text, index) => message(index, text)) });
  const view = await f.api.snapshot('s');
  assert.ok(Buffer.byteLength(JSON.stringify(view.messages), 'utf8') <= budget);
  assert.ok(view.messages.length > 0 && view.messages.length < texts.length);
  assert.deepEqual(view.messages.map(entry => entry.text), texts.slice(-view.messages.length));
  assert.ok(view.messages.every(entry => entry.text.isWellFormed()));
});

test('a single oversized Unicode message stays bounded and preserves complete Unicode characters', async t => {
  const text = '🐳'.repeat(5_400_001) + 'RECENT';
  const f = fixture(t, { events: [message(0, 'old request', 'user'), message(1, text)] });
  const view = await f.api.snapshot('s');
  assert.equal(view.messages.length, 1);
  assert.ok(Buffer.byteLength(JSON.stringify(view.messages), 'utf8') <= budget);
  assert.ok(view.messages[0].text.endsWith('RECENT'));
  assert.ok(view.messages[0].text.length < text.length);
  assert.ok(view.messages[0].text.isWellFormed(), 'truncation must not begin on the low half of a surrogate pair');
});

test('an oversized tool payload cannot bypass the serialized history budget through detail', async t => {
  const f = fixture(t, { events: [{ seq: 1, type: 'tool/call', data: { name: 'large-tool', payload: '界'.repeat(8 * 1024 * 1024) } }] });
  const view = await f.api.snapshot('s');
  assert.equal(view.messages.length, 1);
  assert.equal(view.messages[0].role, 'tool.call');
  assert.ok(view.messages[0].text.includes('large-tool'));
  assert.ok(Buffer.byteLength(JSON.stringify(view.messages), 'utf8') <= budget);
});

for (const [kind, expected] of [['completed', 'idle'], ['error', 'failed'], ['aborted', 'interrupted'], ['max-tokens', 'failed'], ['blocked', 'failed']]) {
  test(`native turn/end ${kind} yields ${expected} without reporting other outcomes as success`, async t => {
    const events = [{ seq: 10, type: 'turn/start', data: {} }, message(11, 'partial or complete response'),
      { seq: 12, type: 'turn/end', data: { reason: { kind } } }];
    const f = fixture(t, { events });
    const view = await f.api.snapshot('s');
    assert.equal(view.turnId, '10');
    assert.equal(view.status, expected);
    assert.equal(view.canStop, false);
    f.agent.status = 'running';
    const running = await f.api.snapshot('s');
    assert.equal(running.status, 'active', 'live native execution dominates a persisted previous terminal event');
    assert.equal(running.canStop, true);
  });
}

test('native pending nextTurn and nextStep queues remain queued after a completed turn', async t => {
  const events = [{ seq: 10, type: 'turn/start', data: {} },
    { seq: 11, type: 'turn/end', data: { reason: { kind: 'completed' } } }];
  const f = fixture(t, { events });
  f.agent.inbox.nextTurn.push({ source: { kind: 'user', rpcId: 'queued-followup' }, content: [{ type: 'text', text: 'next task' }] });
  f.agent.inbox.nextStep.push({ source: { kind: 'user', rpcId: 'queued-steering' }, content: [{ type: 'text', text: 'next step' }] });
  const view = await f.api.snapshot('s');
  assert.equal(view.status, 'queued');
  assert.equal(view.pendingMessages, 2);
  assert.deepEqual(view.acceptedRequestIds, ['queued-followup', 'queued-steering']);
  f.agent.inbox.nextTurn.length = f.agent.inbox.nextStep.length = 0;
  assert.equal((await f.api.snapshot('s')).status, 'idle');
});

test('a newer unfinished native turn cannot inherit the previous completed outcome', async t => {
  const events = [{ seq: 10, type: 'turn/start', data: {} },
    { seq: 11, type: 'turn/end', data: { reason: { kind: 'completed' } } },
    { seq: 12, type: 'turn/start', data: {} }];
  const f = fixture(t, { events });
  const view = await f.api.snapshot('s');
  assert.equal(view.turnId, '12');
  assert.equal(view.status, 'interrupted');
});
