// Test-only preload. Mutates one module only inside the explicitly selected disposable clone.
import fs from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { syncBuiltinESMExports } from 'node:module';
const root = process.env.NTILDE_TERMINFO_TEST_SOURCE_ROOT;
if (!root?.startsWith(join(tmpdir(), 'nt-receipt-')) || !fs.statSync(root).isDirectory()) throw new Error('Expected disposable source-clone root.');
const original = fs.writeFileSync;
fs.writeFileSync = (path, data, ...rest) => {
    const result = original(path, data, ...rest);
    if (typeof path === 'string' && path.startsWith(join(root, 'artifacts', 'terminfo', 'owned-')) && path.endsWith('/raw.json')) {
        const target = join(root, 'scripts', 'terminfo', 'metadata-state.mjs');
        original(target, fs.readFileSync(target, 'utf8') + '\n// Disposable source mutation after collection.\n');
    }
    return result;
};
syncBuiltinESMExports();
