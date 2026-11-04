import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';
import { ownedSourceFiles, ownedSourcePath, assertOwnedSourcesUnchanged } from './owned-sources.mjs';
function fixture(action) {
    const root = mkdtempSync(join(tmpdir(), 'nt-source-unit-')), context = {};
    try {
        for (const [key, file] of Object.entries(ownedSourceFiles)) {
            const bytes = readFileSync(new URL(file === 'terminfo-owned-runner.mjs' ? '../terminfo-owned-runner.mjs' : './' + file, import.meta.url));
            const path = ownedSourcePath(root, file); mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, bytes);
            context[key] = createHash('sha256').update(bytes).digest('hex');
        }
        action({ root, context });
    } finally { rmSync(root, { recursive: true, force: true }); }
}
test('all 60 exact live source/input files still match their initial snapshot', () => fixture(f => {
    assert.equal(Object.keys(ownedSourceFiles).length, 60); assert.doesNotThrow(() => assertOwnedSourcesUnchanged(f.root, f.context));
}));
for (const file of ['metadata-state.mjs', 'worker.mjs', 'ownership.mjs', 'terminfo-owned-runner.mjs']) test('rejects post-snapshot live source change: ' + file, () => fixture(f => {
    writeFileSync(ownedSourcePath(f.root, file), '// Changed only in disposable fixture.');
    assert.throws(() => assertOwnedSourcesUnchanged(f.root, f.context), /Owned source changed before completion/);
}));

test('future measured inputs cannot change the historical published v8 source scope', async () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-legacy-source-unit-'));
    try {
        writeFileSync(join(root, 'owned-sources.mjs'), readFileSync(new URL('./owned-sources.mjs', import.meta.url)));
        const inventory = { ...ownedSourceFiles, futureSourceSha256: 'future.mjs' };
        writeFileSync(join(root, 'owned-source-inventory.json'), JSON.stringify(inventory));
        const module = await import(pathToFileURL(join(root, 'owned-sources.mjs')).href);
        assert.equal(module.ownedSourceFiles.futureSourceSha256, 'future.mjs');
        assert.equal(module.legacyV8SourceFiles.futureSourceSha256, undefined);
        const published = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-03-metadata.json', import.meta.url)));
        const historical = Object.keys(published.archive.fileSha256).filter(name => name.startsWith('sources/')).map(name => name.slice(8));
        assert.equal(historical.length, 51);
        assert.deepEqual(Object.values(module.legacyV8SourceFiles).sort(), historical.sort());
        assert.ok(Object.keys(module.legacyV8SourceFiles).every(key => typeof published.context[key] === 'string'));
    } finally { rmSync(root, { recursive: true, force: true }); }
});
