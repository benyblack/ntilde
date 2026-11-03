import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync, unlinkSync } from 'node:fs';
import { join } from 'node:path';
import { ownedSnapshotReceiptFixture as fixture } from './fixtures/metadata-receipt.mjs';
import { snapshotHash } from './agent-observer.mjs';
test('snapshot-backed v8 binds all 52 measured source/input files and source-tree cleanup', () => fixture(f => {
    const receipt = f.finish(); assert.equal(receipt.schemaVersion, 8); assert.equal(receipt.fixtureRemoved, true);
    assert.equal(Object.keys(f.manifest.hashes).length, 52);
}));
for (const [name, mutate] of [
    ['missing manifest', f => unlinkSync(join(f.output, 'source-snapshot.json'))],
    ['changed manifest bytes', f => writeFileSync(join(f.output, 'source-snapshot.json'), '{}')],
    ['manifest with substituted digest', f => { f.manifest.hashes.workerSourceSha256 = '0'.repeat(64); f.write('source-snapshot.json', f.manifest);
        f.context.sourceSnapshotManifestSha256 = snapshotHash(f.manifest); f.write('context.json', f.context); }],
    ['missing policy', f => { delete f.context.sourceSnapshotPolicy; f.write('context.json', f.context); }],
    ['missing source-tree proof', f => { delete f.proof.sourceSnapshot; f.write('ownership-proof.json', f.proof); }],
    ['source tree remains', f => mkdirSync(f.context.sourceSnapshotRoot)],
    ['wrong source-tree proof', f => { f.proof.sourceSnapshot.root += '-wrong'; f.write('ownership-proof.json', f.proof); }],
    ['source cleanup before App cleanup', f => { f.proof.sourceSnapshot.removedAt = '2000-01-01T00:00:00Z'; f.write('ownership-proof.json', f.proof); }],
    ['uninventoried retained module', f => writeFileSync(join(f.output, 'sources', 'unexpected.mjs'), '// outside measured graph')],
]) test('snapshot receipt refuses ' + name, () => fixture(f => { mutate(f); assert.throws(f.finish); }));
test('new runner archive still forces snapshot requirements after marker and manifest stripping', () => fixture(f => {
    delete f.context.sourceSnapshotPolicy; delete f.context.sourceSnapshotManifestSha256; delete f.context.sourceSnapshotRoot;
    delete f.context.sourceInventorySha256; delete f.proof.sourceSnapshot;
    f.write('context.json', f.context); f.write('ownership-proof.json', f.proof);
    unlinkSync(join(f.output, 'source-snapshot.json')); unlinkSync(join(f.output, 'sources', 'owned-source-inventory.json'));
    assert.throws(f.finish);
}));
