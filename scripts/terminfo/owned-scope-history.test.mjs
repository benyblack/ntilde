import test from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import { snapshotHash } from './agent-observer.mjs';
import { ownedSnapshotReceiptFixture as fixture } from './fixtures/metadata-receipt.mjs';

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
                "export const sourceSnapshotPolicy = 'readonly-owned-source-graph-v3';"));
        }
        const sources = await import(pathToFileURL(join(scripts, 'owned-sources.mjs')).href);
        assert.equal(Object.keys(sources.ownedSourceFiles).length, grow ? 61 : 60);
        assert.equal(sources.sourceSnapshotPolicy, policy ? 'readonly-owned-source-graph-v3' : 'readonly-owned-source-graph-v2');
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
