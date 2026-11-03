// Test-only preload. Mutates one module only inside the explicitly selected disposable clone.
import fs from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { syncBuiltinESMExports } from 'node:module';
const selected = process.env.NTILDE_TERMINFO_TEST_SOURCE_ROOT;
if (!selected) throw new Error('Expected disposable source-clone root.');
const root = fs.realpathSync(selected);
if (!root.startsWith(join(fs.realpathSync(tmpdir()), 'nt-receipt-')) || !fs.statSync(root).isDirectory())
    throw new Error('Expected disposable source-clone root.');
const original = fs.writeFileSync;
fs.writeFileSync = (path, data, ...rest) => {
    const result = original(path, data, ...rest);
    if (typeof path === 'string' && path.endsWith('/worker.json')) {
        const target = join(root, 'scripts', 'terminfo', 'metadata-cases.mjs');
        original(target, fs.readFileSync(target, 'utf8').replaceAll('MD-base-', 'MUTATED-base-'));
    }
    return result;
};
syncBuiltinESMExports();
