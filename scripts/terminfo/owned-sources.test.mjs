import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { createHash } from 'node:crypto';
import { ownedSourceFiles, ownedSourcePath, assertOwnedSourcesUnchanged } from './owned-sources.mjs';
function fixture(action) {
    const root = mkdtempSync(join(tmpdir(), 'nt-source-unit-')), context = {};
    try {
        for (const [key, file] of Object.entries(ownedSourceFiles)) {
            const bytes = readFileSync(new URL(file === 'terminfo-owned.mjs' ? '../terminfo-owned.mjs' : './' + file, import.meta.url));
            const path = ownedSourcePath(root, file); mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, bytes);
            context[key] = createHash('sha256').update(bytes).digest('hex');
        }
        action({ root, context });
    } finally { rmSync(root, { recursive: true, force: true }); }
}
test('all 51 exact live source/input files still match their initial snapshot', () => fixture(f => {
    assert.equal(Object.keys(ownedSourceFiles).length, 51); assert.doesNotThrow(() => assertOwnedSourcesUnchanged(f.root, f.context));
}));
for (const file of ['metadata-state.mjs', 'worker.mjs', 'ownership.mjs', 'terminfo-owned.mjs']) test('rejects post-snapshot live source change: ' + file, () => fixture(f => {
    writeFileSync(ownedSourcePath(f.root, file), '// Changed only in disposable fixture.');
    assert.throws(() => assertOwnedSourcesUnchanged(f.root, f.context), /Owned source changed before completion/);
}));
