import { readdir, stat, mkdir } from 'node:fs/promises';
import { isAbsolute, resolve, dirname, basename, join } from 'node:path';
import { homedir } from 'node:os';

const directory = async path => {
  if (typeof path !== 'string' || !isAbsolute(path) || !(await stat(path)).isDirectory()) throw new Error('Choose an existing absolute directory');
  return resolve(path);
};
const workspace = value => ({ id: value.id ?? value.workspaceId, name: value.title ?? basename(value.path), path: value.path });

export function workspaces(ctx) {
  return {
    list: () => ctx.get('workspaceRegistry').list().map(workspace),
    async browse(path, signal) {
      signal?.throwIfAborted();
      if (!path && process.platform === 'win32') {
        const drives = await Promise.all('ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('').map(async drive => {
          const path = drive + ':\\';
          try { await stat(path); return { name: path, path }; } catch { return null; }
        }));
        return { path: '', parent: null, directories: drives.filter(Boolean) };
      }
      const current = await directory(path || homedir());
      const entries = await readdir(current, { withFileTypes: true });
      signal?.throwIfAborted();
      return { path: current, parent: dirname(current) === current ? null : dirname(current),
        directories: entries.filter(e => e.isDirectory() && !e.isSymbolicLink()).sort((a, b) => a.name.localeCompare(b.name))
          .map(e => ({ name: e.name, path: join(current, e.name) })) };
    },
    async add(args, signal) {
      let path = await directory(args.path);
      if (args.folder) {
        if (typeof args.folder !== 'string' || /[<>"|?*\\/:\x00-\x1f]/.test(args.folder) || ['.', '..'].includes(args.folder)
          || args.folder.trim() !== args.folder || args.folder.endsWith('.') || /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)/i.test(args.folder) || args.folder.length > 128)
          throw new Error('Invalid folder name');
        path = join(path, args.folder);
        await mkdir(path); // Do not silently reuse an existing folder.
      }
      signal?.throwIfAborted();
      const created = await ctx.get('workspaceController').create({ path });
      return workspace(created.workspace);
    },
    directory,
  };
}
