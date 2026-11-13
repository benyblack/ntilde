import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { asciiReflowPublication } from './ascii-reflow-publication.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-12-ascii-reflow/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('retained run independently rederives the complete receipt and published projection', () => {
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), '9ea9604a56c3e751ad350eef9d7ae3ed251a71db991ec6da3086f70928757161', 'the retained archive manifest is pinned');
    const manifest = JSON.parse(manifestBytes);
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-ascii-reflow-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        assert.equal(original.schemaVersion, 56);
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        const projected = asciiReflowPublication(root);
        // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.deepEqual(projected.report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.effectContract.outcome]), [
            ['extensions.reflow', 'inconclusive', 'unsupported'],
        ]);
        assert.equal(projected.report.cases[0].stock.outcome, 'inconclusive');
        assert.equal(projected.report.cases[0].divergence.kind, 'pinned-inconclusive-by-design');
        // The measured geometry transitions and the two-run agreement are part of the projection's observed record.
        const observed = projected.report.cases[0].observed;
        assert.deepEqual(observed.width.initial.geometry, { rows: 40, cols: 140 });
        assert.deepEqual(observed.width.shrunk, { geometry: { rows: 40, cols: 133 }, cursor: [1, 53], history: 0,
            head: observed.width.shrunk.head, matchesModel: true });
        assert.deepEqual(observed.history.shrunk.geometry, { rows: 36, cols: 133 });
        assert.equal(observed.history.shrunk.history, 38);
        assert.equal(observed.history.shrunk.matchesModel, false);
        assert.equal(observed.history.restored.matchesModel, true);
        assert.equal(observed.history.restored.history, 22);
        assert.equal(schemaVersion, 56); assert.equal(fixtureRemoved, true);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
