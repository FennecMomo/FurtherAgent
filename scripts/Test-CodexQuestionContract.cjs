// Read-only compatibility check against an extracted local Codex desktop bundle.
// Usage: node scripts/Test-CodexQuestionContract.cjs <app-shared.js> <fixture.json>
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');

async function main() {
  const source = fs.readFileSync(process.argv[2], 'utf8');
  const fixture = JSON.parse(fs.readFileSync(process.argv[3], 'utf8').replace(/^\uFEFF/, ''));
  const start = source.indexOf('async function Qun(');
  assert(start >= 0, 'Unsupported Codex bundle: submit function not found');
  const end = source.indexOf('function ', start + 15);
  assert(end > start, 'Submit function boundary not found');
  let reachedForwarding = false;
  const context = vm.createContext({
    BB: () => {},
    _V: async () => { reachedForwarding = true; return { result: 'forwarded' }; }
  });
  vm.runInContext(source.slice(start, end), context);
  const manager = {
    getConversation: () => ({}), getConversationCwd: () => 'C:/contract-test'
  };
  const call = restoreMessage => context.Qun({
    manager, conversationId: fixture.conversationId, input: fixture.input,
    restoreMessage, attachments: fixture.attachments,
    clientUserMessageId: fixture.clientUserMessageId
  });
  await assert.rejects(call({ id: 'old-payload', text: 'answer' }), /workspaceRoots/);
  assert.equal(reachedForwarding, false);
  assert.equal(await call(fixture.restoreMessage), 'forwarded');
  assert.equal(reachedForwarding, true);
  console.log('PASS: actual native submit function rejects old missing context and accepts new restore context');
  console.log('Scope: request preparation only; no messages sent, no native card state modified or verified.');
}
main().catch(error => { console.error(error); process.exitCode = 1; });
