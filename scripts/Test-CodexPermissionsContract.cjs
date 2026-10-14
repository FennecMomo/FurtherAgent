// Read-only contract test; executes local bundle functions on synthetic state.
// Usage: node scripts/Test-CodexPermissionsContract.cjs <extracted-bundle.js> <presets.json>
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const source = fs.readFileSync(process.argv[2], 'utf8');
const presets = JSON.parse(fs.readFileSync(process.argv[3], 'utf8').replace(/^\uFEFF/, ''));
const context = vm.createContext({ lS() {} });
for (const name of ['rS', 'oS', 'sS', 'cS', 'aS']) {
  const start = source.indexOf(`function ${name}(`);
  assert(start >= 0, 'Unsupported Codex bundle: missing ' + name);
  const end = source.indexOf('function ', start + 10);
  assert(end > start, 'Unsupported function boundary: ' + name);
  vm.runInContext(source.slice(start, end), context);
}
assert(source.includes('case`thread-follower-update-thread-settings`'), 'Follower settings handler missing');
assert(source.includes('updateThreadSettingsForNextTurn'), 'Next-turn settings path missing');
for (const preset of presets) {
  const model = { mode: 'plan', settings: { model: 'fixture-model', reasoning_effort: 'high', developer_instructions: 'keep' } };
  const state = { cwd: 'C:/fixture', latestModel: 'fixture-model', latestReasoningEffort: 'high', latestCollaborationMode: model,
    latestThreadSettings: { model: 'fixture-model', effort: 'high', disabledPluginIds: ['keep-plugin'], serviceTier: 'auto' } };
  context.rS(state, preset);
  assert.equal(state.latestThreadSettings.permissions, preset.permissions);
  assert.equal(context.aS(state.latestThreadSettings).activePermissionProfile.id, preset.permissions);
  assert.equal(state.latestThreadSettings.approvalPolicy, preset.approvalPolicy);
  assert.equal(state.latestCollaborationMode, model);
  assert.equal(state.latestReasoningEffort, 'high');
  assert.deepEqual(state.latestThreadSettings.disabledPluginIds, ['keep-plugin']);
  assert.equal(state.latestThreadSettings.serviceTier, 'auto');
  console.log('PASS: native settings merge/readback preserves model, mode, effort and unrelated settings: ' + preset.permissions);
}
console.log('Scope: actual native merge functions on fixtures only; no live session permissions changed.');
