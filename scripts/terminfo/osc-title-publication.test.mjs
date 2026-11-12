import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { oscTitlePublication } from './osc-title-publication.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-12-osc-title/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('retained run independently rederives the complete receipt and published projection', () => {
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), 'dc5e4a192f1201deee4d85a4462a0d561cb81aa36d62141da79e6fa0a0703532');
    const manifest = JSON.parse(manifestBytes);
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-osc-title-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        const projected = oscTitlePublication(root);
        // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.deepEqual(projected.report.cases.map(item => [item.caseId, item.pinned.qualifiedOutcome, item.effectContract.outcome]), [
            ['extensions.osc0-icon-title', 'inconclusive', 'supported'], ['extensions.osc1-icon', 'inconclusive', 'supported'],
            ['extensions.osc2-title', 'supported', 'supported'],
        ]);
        assert.ok(projected.report.cases.every(item => item.stock.outcome === 'inconclusive' && item.pinned.terminal.observation.outcome === 'inconclusive'));
        assert.equal(schemaVersion, 53); assert.equal(fixtureRemoved, true);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
