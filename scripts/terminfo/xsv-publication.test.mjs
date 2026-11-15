import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { xsvPublication } from './xsv-publication.mjs';
import { buildXsvGradingReport } from './xsv-grading.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-16-xterm-saved-modes/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('retained run independently rederives the complete receipt and published projection', () => {
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), 'be936c501c9f625bea9f2070f7ea3fa78c5af52b5a6d7adbf0e2aafd8e3fd93b');
    const manifest = JSON.parse(manifestBytes);
    assert.equal(Object.keys(manifest.archives).length, 3, 'the final run and the two retained superseded attempts (both over the five-minute collection budget)');
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-xsv-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        // The retained report is rebuilt from the retained paired journal and worker run log, byte for byte.
        const frames = readFileSync(join(root, 'xsv-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)), retained = readFileSync(join(root, 'xsv-observations.json'));
        const rebuilt = buildXsvGradingReport(frames, JSON.parse(readFileSync(join(root, 'xsv-run.json'))), JSON.parse(retained).runId, JSON.parse(retained).adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), retained), 0);
        const projected = xsvPublication(root);
        // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.equal(schemaVersion, 65); assert.equal(fixtureRemoved, true);
        assert.equal(JSON.parse(readFileSync(join(root, 'context.json'))).ownedQueryPolicy, 'owned-exact-query-paired-raw-state-v4');
        // Both pinned verdicts are the unchanged pinned code's inconclusive over the journaled bytes; the effect contracts carry the graded conclusion and the divergence is recorded.
        assert.deepEqual(projected.report.cases.map(item => [item.caseId, item.pinned.termless.observation.outcome, item.pinned.terminal.observation.outcome, item.effectContract.outcome, item.stock.outcome]), [
            ['modes.xtsave', 'inconclusive', 'inconclusive', 'supported', 'inconclusive'],
            ['modes.xtrestore', 'inconclusive', 'inconclusive', 'supported', 'inconclusive'],
        ]);
        for (const item of projected.report.cases) {
            assert.equal(item.pinned.termless.observation.reason, 'insufficient-evidence');
            assert.deepEqual(item.pinned.termless.observation.evidence, 'consumed');
            assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-effect-supported' });
            assert.equal(Object.values(item.observed.groups).every(group => group.matches && group.unchanged), true, item.caseId);
            assert.equal(item.observed.seed.cells, true);
            // The pinned exchange is silent: a save or restore answers nothing and moves no mode.
            assert.deepEqual(item.observed.groups.pinned.replies, []);
            assert.deepEqual(item.observed.groups.pinned.received.modes, item.observed.groups.pinned.demanded.modes);
            assert.equal(Object.values(item.observed.groups).every(group => group.replies.length === 0), true, 'every group is silent');
        }
        assert.deepEqual(projected.report.cases.map(item => Object.entries(item.observed.groups).filter(([, group]) => group.kind === 'policy').map(([role]) => role)), [
            ['unlisted-values', 'restore-twice', 'cursor-1048', 'origin-restore-homes'], ['unlisted-values', 'restore-twice', 'cursor-1048', 'origin-restore-homes']]);
        assert.deepEqual(projected.summary.stockCollector.counts, { inconclusive: 162, supported: 92, unsupported: 1 });
        assert.deepEqual(projected.report.cases.map(item => Object.keys(item.observed.groups).length), [22, 21]);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
