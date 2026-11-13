import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { selectionColorsPublication } from './selection-colors-publication.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-12-selection-colors/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('retained run independently rederives the complete receipt and published projection', () => {
    const manifestBytes = read('manifest.json');
    const manifest = JSON.parse(manifestBytes);
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-selection-colors-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        const projected = selectionColorsPublication(root);
        // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.deepEqual(projected.report.cases.map(item => [item.caseId, item.conclusion.pinnedTermless, item.contractResult.outcome]), [
            ['extensions.osc17-highlight-bg', 'inconclusive', 'supported'], ['extensions.osc19-highlight-fg', 'inconclusive', 'supported'],
            ['extensions.osc117-reset-highlight-bg', 'inconclusive', 'supported'], ['extensions.osc119-reset-highlight-fg', 'inconclusive', 'supported'],
        ]);
        assert.ok(projected.report.cases.every(item => item.stock.outcome === 'inconclusive'));
        assert.deepEqual(projected.report.cases.map(item => item.divergence?.kind ?? null),
            ['pinned-query-only-unset-default', 'pinned-query-only-unset-default', 'pinned-inconclusive-by-design', 'pinned-inconclusive-by-design']);
        assert.equal(schemaVersion, 54); assert.equal(fixtureRemoved, true);
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('the projection keeps the pinned verdicts, effect verdicts and stock grades in separate fields', () => {
    const report = JSON.parse(read('report.json'));
    assert.equal(report.suiteId, 'ntilde-owned-selection-colors-graded-v1');
    assert.equal(report.adapter.id, 'ntilde-owned-selection-colors-grading-v1');
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-osc-title-grading-v1');
    for (const item of report.cases) {
        assert.equal(item.contractResult.outcome, 'supported');
        assert.equal(item.stock.outcome, 'inconclusive', 'the stock grade is carried unchanged');
        assert.equal(item.pinnedTerminal.byDesign, item.caseId.includes('reset'));
        assert.ok(!('score' in (item.conclusion ?? {})), 'no score promotion claim');
    }
    const summary = JSON.parse(read('summary.json'));
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 167, supported: 87, unsupported: 1 });
    assert.equal(summary.stockCollector.note, 'Stock terminal-path grades; owned effects do not change the stock score.');
});
