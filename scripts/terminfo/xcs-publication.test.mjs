import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { xcsPublication } from './xcs-publication.mjs';
import { buildXcsGradingReport } from './xcs-grading.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-15-xterm-color-stack/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('retained run independently rederives the complete receipt and published projection', () => {
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), 'd25e320f1599042ed11cafe5b347831ec3bdb0a4c92b0f48a27703e7caa80a34');
    const manifest = JSON.parse(manifestBytes);
    assert.equal(Object.keys(manifest.archives).length, 7, 'the final run and the retained superseded attempts');
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-xcs-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        // The retained report is rebuilt from the retained paired journal and worker run log, byte for byte.
        const frames = readFileSync(join(root, 'xcs-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)), retained = readFileSync(join(root, 'xcs-observations.json'));
        const rebuilt = buildXcsGradingReport(frames, JSON.parse(readFileSync(join(root, 'xcs-run.json'))), JSON.parse(retained).runId, JSON.parse(retained).adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), retained), 0);
        const projected = xcsPublication(root);
        // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.equal(schemaVersion, 62); assert.equal(fixtureRemoved, true);
        assert.equal(JSON.parse(readFileSync(join(root, 'context.json'))).ownedQueryPolicy, 'owned-exact-query-paired-raw-state-v4');
        // Both pinned verdicts are the unchanged pinned code's inconclusive over the journaled bytes; the effect contracts carry the graded conclusion and the divergence is recorded.
        assert.deepEqual(projected.report.cases.map(item => [item.caseId, item.pinned.termless.observation.outcome, item.pinned.terminal.observation.outcome, item.effectContract.outcome, item.stock.outcome]), [
            ['modes.xtpushcolors', 'inconclusive', 'inconclusive', 'supported', 'inconclusive'],
            ['modes.xtpopcolors', 'inconclusive', 'inconclusive', 'supported', 'inconclusive'],
        ]);
        for (const item of projected.report.cases) {
            assert.equal(item.pinned.termless.observation.reason, 'insufficient-evidence');
            assert.deepEqual(item.pinned.termless.observation.evidence, 'consumed');
            assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-effect-supported' });
            assert.equal(Object.values(item.observed.groups).every(group => group.matches && group.unchanged), true, item.caseId);
            assert.equal(item.observed.seed.cells, true);
            // The pinned exchange is silent: a push or pop answers nothing.
            assert.deepEqual(item.observed.groups.pinned.received, []);
        }
        assert.deepEqual(projected.report.cases.map(item => Object.entries(item.observed.groups).filter(([, group]) => group.kind === 'policy').map(([role]) => role)), [
            ['extended-palette'], ['pop-empty', 'extended-palette']]);
        assert.deepEqual(projected.summary.stockCollector.counts, { inconclusive: 163, supported: 91, unsupported: 1 });
        assert.deepEqual(projected.report.cases.map(item => Object.keys(item.observed.groups).length), [15, 16]);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
