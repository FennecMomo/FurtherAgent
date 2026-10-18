import { randomBytes, createHash } from 'node:crypto';

// A live remote viewer may own a native waterfall. Losing that viewer hands
// the unanswered request back to DSH; a disconnected phone never grants access.
export function interactions(ctx, changed, { leaseMs = 45000 } = {}) {
  const leases = new Map();
  const observerStates = new Map();
  const pending = new Map();
  const continuedSubmissions = new Set();
  const releases = [];
  const isRoot = agent => !!agent && ctx.agents.get(agent.id) === agent && ctx.agents.roots?.().includes(agent) === true;
  const subscribed = id => {
    const owners = leases.get(id);
    if (!owners) return false;
    for (const [owner, until] of owners) if (until <= Date.now()) owners.delete(owner);
    if (!owners.size) { leases.delete(id); return false; }
    return true;
  };
  const finish = (entry, result, error) => {
    if (!pending.delete(entry.prompt.id)) return false;
    entry.request.signal?.removeEventListener('abort', entry.abort);
    changed(entry.sessionId);
    if (error) entry.reject(error); else entry.resolve(result);
    return true;
  };
  const handoff = entry => {
    if (entry.request.signal?.aborted) return entry.abort();
    // Remove before invoking next(): the local UI becomes the only owner.
    if (!pending.has(entry.prompt.id)) return;
    finish(entry, Promise.resolve().then(() => {
      if (!entry.request.signal?.aborted) return entry.next();
      if (entry.kind === 'approval') return 'cancelled';
      throw new Error('Question cancelled');
    }));
  };
  const timer = setInterval(() => {
    for (const id of leases.keys()) subscribed(id);
    for (const [key, state] of observerStates) if (state.updated + leaseMs * 4 < Date.now()) observerStates.delete(key);
    for (const entry of pending.values()) if (!subscribed(entry.sessionId)) handoff(entry);
  }, Math.min(1000, leaseMs));
  timer.unref();
  function observe({ id, observerId, observed, observerRevision }) {
    if (typeof id !== 'string' || !id || typeof observerId !== 'string' || !observerId || observerId.length > 128 || typeof observed !== 'boolean'
      || !Number.isSafeInteger(observerRevision) || observerRevision < 0)
      throw new Error('Invalid viewer lease');
    const key = JSON.stringify([id, observerId]);
    const previous = observerStates.get(key);
    if (previous && (observerRevision < previous.revision || observerRevision === previous.revision && observed !== previous.observed))
      return { observed: previous.observed, stale: true };
    if (!previous && observerStates.size >= 4096) throw new Error('Too many viewer identities');
    if (observed) {
      for (const session of leases.keys()) subscribed(session);
      if (!leases.has(id) && leases.size >= 128) throw new Error('Too many observed sessions');
      const owners = leases.get(id) ?? new Map();
      if (!owners.has(observerId) && owners.size >= 16) throw new Error('Too many session viewers');
      owners.set(observerId, Date.now() + leaseMs); leases.set(id, owners);
    } else {
      leases.get(id)?.delete(observerId);
      if (!subscribed(id)) for (const entry of pending.values()) if (entry.sessionId === id) handoff(entry);
    }
    observerStates.set(key, { revision: observerRevision, observed, updated: Date.now() });
    return { observed };
  }
  const questionPrompt = (id, questions) => ({ id, kind: 'item/tool/requestUserInput', title: 'DSH 需要你的回答', detail: '', payload: { questions } });
  const install = (event, kind) => ctx.on(event, (request, next) => {
    const agent = request.agent;
    if (!isRoot(agent) || !subscribed(agent.session.id)) return next();
    if (pending.size >= 128) return next();
    if (request.signal?.aborted) return kind === 'approval' ? 'cancelled' : Promise.reject(new Error('Question cancelled'));
    const id = 'dsh:' + randomBytes(16).toString('hex');
    const prompt = kind === 'approval'
      ? { id, kind: 'dsh/approval', title: 'DSH 工具权限', detail: request.displayReason ?? request.reason ?? request.toolName ?? '',
        payload: { toolName: request.toolName, callId: request.callId, reason: request.displayReason ?? request.reason ?? '' } }
      : questionPrompt(id, request.questions);
    return new Promise((resolve, reject) => {
      const entry = { prompt, kind, sessionId: agent.session.id, request, next, resolve, reject };
      entry.abort = () => finish(entry, kind === 'approval' ? 'cancelled' : undefined, kind === 'question' ? new Error('Question cancelled') : undefined);
      pending.set(id, entry);
      request.signal?.addEventListener('abort', entry.abort, { once: true });
      if (request.signal?.aborted) entry.abort(); else changed(entry.sessionId);
    });
  }, { global: true, prepend: true });
  const available = typeof ctx.on === 'function' && typeof ctx.agents.roots === 'function';
  if (available) {
    releases.push(install('approval/request', 'approval'));
    releases.push(install('user-questions/request', 'question'));
  }
  const continuedId = (id, callId) => 'dsh:continued:' + createHash('sha256').update(JSON.stringify([id, callId])).digest('hex');
  function continued(id, values) {
    const agent = ctx.agents.get(id);
    if (!isRoot(agent)) return [];
    return (values?.userQuestions?.active ?? []).filter(q => q.state === 'continued'
      && ![...pending.values()].some(p => p.sessionId === id && p.kind === 'question' && p.request.wait?.callId === q.callId))
      .map(q => ({ callId: q.callId, prompt: questionPrompt(continuedId(id, q.callId), q.questions) }));
  }
  function answers(questions, response) {
    const input = response?.answers;
    if (!input || typeof input !== 'object' || Array.isArray(input)) throw new Error('Please answer every question');
    if (Object.keys(input).length !== questions.length) throw new Error('Question identity mismatch');
    return { answers: questions.map(q => {
      if (!Object.hasOwn(input, q.id)) throw new Error('Please answer every question');
      const values = input[q.id]?.answers;
      if (!Array.isArray(values) || !values.length || values.some(v => typeof v !== 'string' || !v.trim() || v.length > 4000) || new Set(values).size !== values.length)
        throw new Error('Invalid question answer');
      const labels = new Set((q.options ?? []).map(o => o.label));
      const selected = values.filter(v => labels.has(v));
      const custom = values.filter(v => !labels.has(v));
      if (!q.multiSelect && selected.length + custom.length !== 1 || custom.length > 1)
        throw new Error('Invalid answer selection count');
      return { id: q.id, selected, ...(custom.length ? { custom: custom[0] } : {}) };
    }) };
  }
  return {
    available, observe,
    prompts: (id, values) => [...pending.values()].filter(p => p.sessionId === id).map(p => p.prompt).concat(continued(id, values).map(q => q.prompt)),
    async respond(args, values) {
      const entry = pending.get(args.requestId);
      if (entry) {
        if (entry.sessionId !== args.id || !subscribed(args.id)) throw new Error('This prompt is no longer owned by this session viewer');
        if (entry.request.signal?.aborted) { entry.abort(); throw new Error('Prompt cancelled'); }
        let result;
        if (entry.kind === 'approval') {
          if (!['accept', 'decline'].includes(args.decision)) throw new Error('Only allow-once and reject are supported');
          result = args.decision === 'accept' ? 'allowed-once' : 'rejected';
        } else result = answers(entry.request.questions, args.response);
        if (!finish(entry, result)) throw new Error('Prompt already settled');
        return { accepted: true };
      }
      const continuedQuestion = continued(args.id, values).find(q => q.prompt.id === args.requestId);
      if (!continuedQuestion || !subscribed(args.id) || continuedSubmissions.has(args.requestId)) throw new Error('Prompt expired or already settled');
      const batch = answers(continuedQuestion.prompt.payload.questions, args.response);
      // Reserve before native I/O, including across separate FurtherAgent Hosts.
      continuedSubmissions.add(args.requestId);
      const accepted = await ctx.get('userQuestions').answer(ctx.agents.get(args.id), continuedQuestion.callId, batch);
      if (accepted === false) throw new Error('Question expired before the answer was accepted');
      changed(args.id);
      return { accepted: true };
    },
    close() {
      clearInterval(timer);
      for (const release of releases) release?.();
      for (const entry of [...pending.values()]) handoff(entry);
      leases.clear();
      observerStates.clear();
      continuedSubmissions.clear();
    },
  };
}
