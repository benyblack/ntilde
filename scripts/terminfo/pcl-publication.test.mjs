import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { pclPublication } from './pcl-publication.mjs';
import { buildPclGradingReport } from './pcl-grading.mjs';
import { pclCaseIds } from './pcl-contracts.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-20-pointer-color/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('retained run independently rederives the complete receipt and published projection', () => {
    const manifestBytes = read('manifest.json');
    const manifest = JSON.parse(manifestBytes);
    assert.deepEqual(Object.keys(manifest.archives), ['owned-run.zip', 'superseded/attempt-1.zip'], 'the final run and the one retained failed attempt');
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const attempts = JSON.parse(read('attempts.json'));
    assert.deepEqual(attempts.attempts.map(entry => entry.file), ['superseded/attempt-1.zip']);
    const root = mkdtempSync(join(tmpdir(), 'nt-pcl-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        // The retained report is rebuilt from the retained paired journal and worker run log, byte for byte.
        const frames = readFileSync(join(root, 'pcl-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)), retained = readFileSync(join(root, 'pcl-observations.json'));
        const rebuilt = buildPclGradingReport(frames, JSON.parse(readFileSync(join(root, 'pcl-run.json'))), JSON.parse(retained).runId, JSON.parse(retained).adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), retained), 0);
        const projected = pclPublication(root, { appRevision: JSON.parse(read('summary.json')).appRevision, scriptsRevision: JSON.parse(read('summary.json')).scriptsRevision });
        // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.deepEqual(projected.report.cases.map(item => item.caseId), pclCaseIds);
        for (const item of projected.report.cases) {
            assert.equal(item.conclusion.outcome, 'inconclusive', 'the pinned verdict stays inconclusive');
            assert.equal(item.conclusion.reason, 'no-response');
            assert.equal(item.effectContract.outcome, 'unsupported', 'the separately identified effect contract');
            assert.equal(item.contractResult.outcome, 'unsupported');
            assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'pinned-inconclusive-effect-unsupported' });
            assert.equal(item.pinned.termless.observation.outcome, 'inconclusive');
            assert.equal(item.pinned.terminal.observation.evidence, 'consumed');
            assert.equal(item.stock.outcome, 'inconclusive', 'the stock collector grade is unchanged');
            assert.equal(item.stock.evidence, 'consumed');
            assert.ok(Object.values(item.observed.groups).every(group => group.matches === true), `${item.caseId} every group held, controls included`);
            assert.equal(Object.keys(item.observed.groups).length, 14);
        }
        assert.deepEqual(projected.summary.stockCollector.counts, { inconclusive: 157, supported: 97, unsupported: 1 });
        assert.equal(schemaVersion, 78); assert.equal(fixtureRemoved, true);
        assert.match(projected.summary.appRevision, /^[0-9a-f]{40}$/);
        assert.equal(projected.summary.appRevision, projected.summary.scriptsRevision);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
