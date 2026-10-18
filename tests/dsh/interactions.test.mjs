import test from 'node:test';
import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';
import { service } from '../../plugins/furtheragent-dsh/bridge.mjs';

function fixture(config = {}) {
  const hooks = new Map(); const events = []; const values = {}; const answers = [];
  const agent = { id: 's', session: { id: 's' }, status: 'running' };
  const ctx = {
    agents: { get: id => id === 's' ? agent : undefined, roots: () => [agent] },
    on(name, fn, options) {
      assert.equal(options.global, true);
      if (!hooks.has(name)) hooks.set(name, new Set());
      hooks.get(name).add(fn); return () => hooks.get(name).delete(fn);
    },
    get: name => name === 'userQuestions' ? { answer: async (owner, callId, batch) => { answers.push({ owner, callId, batch }); } } : undefined,
    sessionQuery: { observeSession: async () => ({ events, header: { title: 'Native' }, cursor: events.length,
      projections: { values }, [Symbol.dispose]() {} }) },
    sessionController: {},
  };
  const api = service(ctx, config);
  const request = (name, fields = {}, next = () => 'local') => {
    const handler = [...hooks.get(name)][0];
    return handler({ agent, ...fields }, next);
  };
  let revision = 0;
  const observe = (observed = true, owner = 'phone') => api.call('observe', { id: 's', observerId: owner, observed, observerRevision: ++revision });
  return { api, ctx, hooks, agent, events, values, answers, request, observe };
}

test('native hooks preserve local UI when unobserved and never intercept subagents or other Hosts', async () => {
  const f = fixture();
  try {
    assert.equal(f.request('approval/request'), 'local');
    await f.observe();
    assert.equal(f.request('approval/request', { agent: { ...f.agent } }), 'local');
    f.ctx.agents.roots = () => [];
    assert.equal(f.request('approval/request'), 'local');
  } finally { f.api.close(); }
});

test('remote approval resolves the native waterfall once, validates identity and allows only this invocation', async () => {
  const f = fixture();
  try {
    await f.observe();
    const result = f.request('approval/request', { toolName: 'shell', reason: 'Run a command' });
    const prompt = (await f.api.snapshot('s')).prompts[0];
    assert.equal(prompt.kind, 'dsh/approval');
    await assert.rejects(f.api.call('respond', { id: 'other', requestId: prompt.id, decision: 'accept' }));
    await assert.rejects(f.api.call('respond', { id: 's', requestId: prompt.id, decision: 'acceptForSession' }));
    await f.api.call('respond', { id: 's', requestId: prompt.id, decision: 'accept' });
    assert.equal(await result, 'allowed-once');
    await assert.rejects(f.api.call('respond', { id: 's', requestId: prompt.id, decision: 'accept' }));
    assert.equal((await f.api.snapshot('s')).prompts.length, 0);
  } finally { f.api.close(); }
});

test('losing the last viewer hands unanswered requests to local DSH, preserving other viewer claims', async () => {
  const f = fixture(); let local = 0;
  try {
    await f.observe(); await f.observe(true, 'desktop');
    const result = f.request('approval/request', {}, () => { local++; return 'rejected'; });
    await f.observe(false); assert.equal(local, 0);
    await f.observe(false, 'desktop');
    assert.equal(await result, 'rejected'); assert.equal(local, 1);
    assert.equal((await f.api.snapshot('s')).prompts.length, 0);
  } finally { f.api.close(); }
});

test('viewer lease expiry hands off without granting and reclaims expired capacity', async () => {
  const f = fixture({ leaseMs: 10 }); let local = 0;
  try {
    for (let i = 0; i < 16; i++) await f.observe(true, 'old-' + i);
    const result = f.request('approval/request', {}, () => { local++; return 'rejected'; });
    await delay(40);
    assert.equal(await result, 'rejected'); assert.equal(local, 1);
    await f.observe(true, 'fresh');
    const next = f.request('approval/request');
    const prompt = (await f.api.snapshot('s')).prompts[0];
    await f.api.call('respond', { id: 's', requestId: prompt.id, decision: 'decline' });
    assert.equal(await next, 'rejected');
  } finally { f.api.close(); }
});

test('out-of-order long polls cannot revive a viewer after it has disconnected', async () => {
  const f = fixture();
  try {
    await f.api.call('observe', { id: 's', observerId: 'phone', observed: true, observerRevision: 1 });
    await f.api.call('observe', { id: 's', observerId: 'phone', observed: false, observerRevision: 2 });
    assert.equal((await f.api.call('observe', { id: 's', observerId: 'phone', observed: true, observerRevision: 1 })).stale, true);
    assert.equal((await f.api.call('observe', { id: 's', observerId: 'phone', observed: true, observerRevision: 2 })).stale, true);
    assert.equal(f.request('approval/request'), 'local');
  } finally { f.api.close(); }
});

test('an abort between handoff and its microtask cannot reopen cancelled local UI', async () => {
  const f = fixture(); let local = 0;
  try {
    await f.observe();
    const abort = new AbortController();
    const result = f.request('approval/request', { signal: abort.signal }, () => { local++; return 'allowed-once'; });
    const disconnected = f.observe(false);
    abort.abort(); await disconnected;
    assert.equal(await result, 'cancelled'); assert.equal(local, 0);
  } finally { f.api.close(); }
});

test('native cancellation removes pending approval/question without opening stale local UI', async () => {
  const f = fixture(); let local = 0;
  try {
    await f.observe();
    const abort = new AbortController();
    const approval = f.request('approval/request', { signal: abort.signal }, () => { local++; });
    abort.abort(); assert.equal(await approval, 'cancelled');
    const abortQuestion = new AbortController();
    const question = f.request('user-questions/request', { signal: abortQuestion.signal, questions: [{ id: 'q', question: 'Why?' }] }, () => { local++; });
    const rejects = assert.rejects(question, /cancelled/);
    abortQuestion.abort(); await rejects;
    assert.equal(local, 0); assert.equal((await f.api.snapshot('s')).prompts.length, 0);
  } finally { f.api.close(); }
});

test('native multi-question answers validate all identities and preserve selected options and custom text', async () => {
  const f = fixture();
  try {
    await f.observe();
    const questions = [{ id: 'q1', question: 'Choose', multiSelect: true, options: [{ label: 'A' }, { label: 'B' }] }, { id: 'q2', question: 'Explain' }];
    const result = f.request('user-questions/request', { questions });
    const prompt = (await f.api.snapshot('s')).prompts[0];
    const reply = answers => f.api.call('respond', { id: 's', requestId: prompt.id, response: { answers } });
    await assert.rejects(reply({ q1: { answers: ['A'] } }));
    await assert.rejects(reply({ q1: { answers: ['A', 'A'] }, q2: { answers: ['text'] } }));
    await assert.rejects(reply({ q1: { answers: ['A'] }, q2: { answers: ['x', 'y'] } }));
    await reply({ q1: { answers: ['A', 'B', 'other'] }, q2: { answers: ['text'] } });
    assert.deepEqual(await result, { answers: [{ id: 'q1', selected: ['A', 'B'], custom: 'other' }, { id: 'q2', selected: [], custom: 'text' }] });
  } finally { f.api.close(); }
});

test('continued questions use native answer once across concurrent independent controllers', async () => {
  const f = fixture();
  try {
    await f.observe();
    f.values.userQuestions = { active: [{ callId: 'tool-1', state: 'continued', questions: [{ id: 'q', question: 'Continue?' }] }] };
    const prompt = (await f.api.snapshot('s')).prompts[0];
    const args = { id: 's', requestId: prompt.id, response: { answers: { q: { answers: ['Yes'] } } } };
    const results = await Promise.allSettled([f.api.call('respond', args), f.api.call('respond', args)]);
    assert.equal(results.filter(r => r.status === 'fulfilled').length, 1);
    assert.equal(f.answers.length, 1); assert.equal(f.answers[0].owner, f.agent);
  } finally { f.api.close(); }
});

test('native continued-question replies remain readable human history rather than hidden tool context', async () => {
  const f = fixture();
  try {
    f.events.push({ type: 'user/message', seq: 1, data: { id: 'answer', source: { kind: 'user-question-reply' }, content: [{ type: 'text', text: JSON.stringify({
      kind: 'answer_to_pending_question', questions: [{ id: 'q', question: '选哪个？' }], answers: [{ id: 'q', selected: ['A'], custom: '补充' }],
    }) }] } });
    assert.deepEqual((await f.api.snapshot('s')).messages, [{ role: 'user', text: '选哪个？\n回答：A、补充' }]);
  } finally { f.api.close(); }
});

test('approval arrival wakes a cursor-stable watch and bridge shutdown hands off questions', async () => {
  const f = fixture(); let local = 0;
  await f.observe();
  const initial = await f.api.snapshot('s');
  const watch = f.api.call('watch', { id: 's', cursor: initial.cursor, streamRevision: initial.streamRevision, status: initial.status, pendingMessages: initial.pendingMessages });
  await delay(5);
  const result = f.request('user-questions/request', { questions: [{ id: 'q', question: 'Choose?' }] }, () => { local++; return { answers: [] }; });
  assert.equal((await watch).prompts.length, 1);
  f.api.close(); assert.deepEqual(await result, { answers: [] }); assert.equal(local, 1);
  assert.ok([...f.hooks.values()].every(set => set.size === 0));
});
