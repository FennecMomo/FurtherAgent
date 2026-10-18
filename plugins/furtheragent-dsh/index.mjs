import { startBridge } from './bridge.mjs';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
export const name = 'furtheragent-dsh';
export const inject = ['sessionController', 'sessionQuery', 'agents', 'systemPrompt'];
export async function apply(ctx, config) {
  let disposed = false; let bridge;
  ctx.on('dispose', async () => { disposed = true; await bridge?.close(); });
  let hostVersion = process.env.DSH_CLIENT_VERSION;
  if (!hostVersion) {
    try { hostVersion = JSON.parse(readFileSync(join(dirname(process.argv[1]), '..', 'package.json'), 'utf8')).version; } catch { /* Report unknown instead of using the unrelated PATH CLI. */ }
  }
  const bridgeVersion = JSON.parse(readFileSync(new URL('./package.json', import.meta.url), 'utf8')).version;
  bridge = await startBridge(ctx, { ...config, hostVersion, bridgeVersion });
  if (disposed) await bridge.close();
}
