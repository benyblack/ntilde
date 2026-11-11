import test from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync, unlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import { snapshotHash } from './agent-observer.mjs';
import { ownedSnapshotReceiptFixture as fixture } from './fixtures/metadata-receipt.mjs';
import { historicalArchive } from './fixtures/captured-sources.mjs';
import { completeOwnedReceipt } from './receipt.mjs';

for (const [name, grow, policy] of [
    ['future inventory', true, false],
    ['future active policy', false, true],
    ['future inventory and policy', true, true],
]) test(name + ' cannot reinterpret a complete captured v8 receipt', async () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-v8-history-'));
    try {
        const scripts = join(root, 'terminfo');
        cpSync(new URL('./', import.meta.url), scripts, { recursive: true });
        if (grow) {
            const path = join(scripts, 'owned-source-inventory.json');
            const inventory = JSON.parse(readFileSync(path));
            inventory.futureSourceSha256 = 'future.mjs'; writeFileSync(path, JSON.stringify(inventory));
        }
        if (policy) {
            const path = join(scripts, 'owned-sources.mjs');
            writeFileSync(path, readFileSync(path, 'utf8').replace(/export const sourceSnapshotPolicy = [^;]+;/,
                "export const sourceSnapshotPolicy = 'readonly-owned-source-graph-v38';"));
        }
        const sources = await import(pathToFileURL(join(scripts, 'owned-sources.mjs')).href);
        assert.equal(Object.keys(sources.ownedSourceFiles).length, grow ? 270 : 269);
        assert.equal(sources.sourceSnapshotPolicy, policy ? 'readonly-owned-source-graph-v38' : 'readonly-owned-source-graph-v37');
        const published = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-04-metadata-snapshot.json', import.meta.url)));
        const paths = Object.keys(published.archive.fileSha256).filter(name => name.startsWith('sources/')).map(name => name.slice(8));
        assert.equal(paths.length, 52);
        assert.deepEqual(Object.values(sources.capturedV8SourceFiles).sort(), paths.sort());
        assert.equal(sources.capturedV8SourcePolicy, published.sourceSnapshot.policy);
        assert.ok(Object.keys(sources.capturedV8SourceFiles).every(key => typeof published.context[key] === 'string'));
        const next = await import(pathToFileURL(join(scripts, 'receipt.mjs')).href);
        fixture(f => {
            assert.equal(Object.keys(f.manifest.hashes).length, 52);
            let receipt;
            assert.doesNotThrow(() => { receipt = next.completeOwnedReceipt(f.output, join(f.output, 'removed'), f.evidence); });
            assert.equal(receipt.schemaVersion, 8); assert.equal(receipt.fixtureRemoved, true);
            assert.deepEqual(receipt.metadataEvidence.counts, { passed: 10, inconclusive: 2, failed: 1 });
        });
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('an expanded graph cannot silently acquire a v8 receipt under the historical policy', () => fixture(f => {
    const hash = bytes => createHash('sha256').update(bytes).digest('hex');
    const future = Buffer.from('// Synthetic future measured module, never executed.\n');
    writeFileSync(join(f.output, 'sources', 'future.mjs'), future);
    f.context.futureSourceSha256 = hash(future);
    const inventoryPath = join(f.output, 'sources', 'owned-source-inventory.json');
    const inventory = JSON.parse(readFileSync(inventoryPath)); inventory.futureSourceSha256 = 'future.mjs';
    writeFileSync(inventoryPath, JSON.stringify(inventory));
    f.context.sourceInventorySha256 = hash(readFileSync(inventoryPath));
    f.manifest.hashes = { ...f.manifest.hashes, sourceInventorySha256: f.context.sourceInventorySha256,
        futureSourceSha256: f.context.futureSourceSha256 };
    f.write('source-snapshot.json', f.manifest);
    f.context.sourceSnapshotManifestSha256 = snapshotHash(f.manifest); f.write('context.json', f.context);
    assert.throws(f.finish, /Invalid or incomplete v8/);
}));

for (const name of ['2026-11-03-metadata', '2026-11-04-metadata-snapshot', '2026-11-04-reset-observations', '2026-11-04-input-observations'])
    test('current finalizer reproduces every frozen v8/v9 binding: ' + name, () => {
        const output = mkdtempSync(join(tmpdir(), 'nt-v8-reproduce-'));
        try {
            for (const [file, bytes] of historicalArchive(name)) {
                assert.ok(!file.split('/').includes('..') && !file.startsWith('/'));
                const target = join(output, file); mkdirSync(join(target, '..'), { recursive: true }); writeFileSync(target, bytes);
            }
            const original = JSON.parse(readFileSync(join(output, 'cell-receipt.json')));
            const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
            unlinkSync(join(output, 'cell-receipt.json'));
            const current = completeOwnedReceipt(output, join(output, 'already-removed-fixture'), evidence);
            current.completedAt = original.completedAt;
            assert.deepEqual(current, original);
        } finally { rmSync(output, { recursive: true, force: true }); }
    });

for (const [name, grow, policy] of [['inventory', true, false], ['policy', false, true], ['inventory and policy', true, true]])
    test('future ' + name + ' preserves actual complete60-source v9 archive and full14-suite finalization', async () => {
        const root = mkdtempSync(join(tmpdir(), 'nt-v9-future-'));
        try {
            const scripts = join(root, 'modules'); cpSync(new URL('./', import.meta.url), scripts, { recursive: true });
            if (grow) {
                const path = join(scripts, 'owned-source-inventory.json'), inventory = JSON.parse(readFileSync(path));
                inventory.futureSourceSha256 = 'future.mjs'; writeFileSync(path, JSON.stringify(inventory));
            }
            if (policy) {
                const path = join(scripts, 'owned-sources.mjs');
                writeFileSync(path, readFileSync(path, 'utf8').replace(/export const sourceSnapshotPolicy = [^;]+;/,
                    "export const sourceSnapshotPolicy = 'readonly-owned-source-graph-v38';"));
            }
            const sources = await import(pathToFileURL(join(scripts, 'owned-sources.mjs')).href);
            assert.equal(Object.keys(sources.capturedV9SourceFiles).length, 60);
            assert.equal(sources.capturedV9SourcePolicy, 'readonly-owned-source-graph-v2');
            assert.equal(Object.keys(sources.ownedSourceFiles).length, grow ? 270 : 269);
            const next = await import(pathToFileURL(join(scripts, 'receipt.mjs')).href), output = join(root, 'evidence'); mkdirSync(output);
            const archived = historicalArchive('2026-11-04-input-observations');
            for (const [file, bytes] of archived) { const target = join(output, file); mkdirSync(join(target, '..'), { recursive: true }); writeFileSync(target, bytes); }
            const original = JSON.parse(archived.get('cell-receipt.json'));
            const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
            unlinkSync(join(output, 'cell-receipt.json'));
            const current = next.completeOwnedReceipt(output, join(output, 'removed-fixture'), evidence);
            current.completedAt = original.completedAt; assert.deepEqual(current, original); assert.equal(current.schemaVersion, 9);
        } finally { rmSync(root, { recursive: true, force: true }); }
    });
