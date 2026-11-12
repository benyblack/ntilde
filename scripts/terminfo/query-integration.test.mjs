import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, cpSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import { queryReceiptFixture } from './fixtures/query-receipt.mjs';
import { tmpdir } from 'node:os';
import { ownedSourceFiles, sourceSnapshotPolicy } from './owned-sources.mjs';
import { historicalArchive } from './fixtures/captured-sources.mjs';
import { completeOwnedReceipt } from './receipt.mjs';

test('live query graph includes all query and shared-scanner dependencies under its next fixed policy', () => {
    assert.equal(sourceSnapshotPolicy, 'readonly-owned-source-graph-v48');
    assert.equal(Object.keys(ownedSourceFiles).length, 317);
    for (const file of ['queries.mjs', 'query-capture.mjs', 'query-collector.mjs', 'query-receipt.mjs', 'query-witness-cases.mjs', 'query-witness-frame.mjs', 'source-imports.mjs'])
        assert.ok(Object.values(ownedSourceFiles).includes(file), file);
});
test('retained query artifact cannot silently acquire an otherwise complete frozen schema10 receipt', () => {
    const output = mkdtempSync(join(tmpdir(), 'nt-query-downgrade-'));
    try {
        const files = historicalArchive('2026-11-04-image-observations');
        for (const [file, bytes] of files) {
            const target = join(output, file); mkdirSync(dirname(target), { recursive: true }); writeFileSync(target, bytes);
        }
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = JSON.parse(files.get('cell-receipt.json'));
        rmSync(join(output, 'cell-receipt.json')); writeFileSync(join(output, 'query-snapshots.jsonl'), '');
        assert.throws(() => completeOwnedReceipt(output, join(output, 'removed-fixture'), evidence), /query|Query|Invalid/);
    } finally { rmSync(output, { recursive: true, force: true }); }
});

for (const [label, grow, policy] of [['inventory', true, false], ['policy', false, true], ['inventory and policy', true, true]])
    test('future active ' + label + ' preserves the fixed74-source complete query receipt', () => queryReceiptFixture(async f => {
        const root = mkdtempSync(join(tmpdir(), 'nt-query-future-'));
        try {
            const modules = join(root, 'modules'); cpSync(new URL('./', import.meta.url), modules, { recursive: true });
            if (grow) {
                const path = join(modules, 'owned-source-inventory.json'), inventory = JSON.parse(readFileSync(path));
                inventory.futureSourceSha256 = 'future.mjs'; writeFileSync(path, JSON.stringify(inventory));
            }
            if (policy) {
                const path = join(modules, 'owned-sources.mjs'); writeFileSync(path, readFileSync(path, 'utf8').replace(/export const sourceSnapshotPolicy = [^;]+;/,
                    "export const sourceSnapshotPolicy = 'readonly-owned-source-graph-v49';"));
            }
            const sources = await import(pathToFileURL(join(modules, 'owned-sources.mjs')).href);
            assert.equal(Object.keys(sources.ownedSourceFiles).length, grow ? 318 : 317); assert.equal(Object.keys(sources.capturedV11SourceFiles).length, 74);
            assert.equal(sources.sourceSnapshotPolicy, policy ? 'readonly-owned-source-graph-v49' : 'readonly-owned-source-graph-v48');
            const next = await import(pathToFileURL(join(modules, 'receipt.mjs')).href);
            const receipt = next.completeOwnedReceipt(f.output, join(f.output, 'removed-fixture'), f.evidence);
            assert.equal(receipt.schemaVersion, 11); assert.deepEqual(receipt.queryEvidence.counts, { matched: 40 });
        } finally { rmSync(root, { recursive: true, force: true }); }
    }));
test('a coherently expanded75-source graph cannot acquire the frozen query policy', () => queryReceiptFixture(f => {
    const hash = bytes => createHash('sha256').update(bytes).digest('hex'), file = join(f.output, 'sources', 'owned-source-inventory.json');
    const inventory = JSON.parse(readFileSync(file)); inventory.futureSourceSha256 = 'future.mjs';
    const future = Buffer.from('// Never executed synthetic extra source.\n'); writeFileSync(join(f.output, 'sources', 'future.mjs'), future);
    writeFileSync(file, JSON.stringify(inventory)); f.context.sourceInventorySha256 = hash(readFileSync(file)); f.context.futureSourceSha256 = hash(future);
    f.manifest.hashes.sourceInventorySha256 = f.context.sourceInventorySha256; f.manifest.hashes.futureSourceSha256 = f.context.futureSourceSha256;
    f.write('source-snapshot.json', f.manifest); f.context.sourceSnapshotManifestSha256 = hash(readFileSync(join(f.output, 'source-snapshot.json')));
    f.write('context.json', f.context); assert.throws(f.finish, /Invalid/);
}));
