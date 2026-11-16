import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { osc633ImplementationPublication, measuredAppRevision, measuredScriptsRevision } from './osc633-implementation-publication.mjs';
import { buildOsc633GradingReport } from './osc633-grading.mjs';
import { osc633CaseIds } from './osc633-grading-collector.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-16-osc633-implementation/', import.meta.url));
const baselineDir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-16-osc633/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('the implemented OSC 633 run independently rederives the receipt, paired journal and publication', () => {
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), 'fa1f63e5209c05dc534dc9f6f092c4bf16535c574df6fb6037403464626446ce');
    const manifest = JSON.parse(manifestBytes);
    assert.deepEqual(Object.keys(manifest.archives), ['owned-run.zip']);
    assert.equal(hash(read('owned-run.zip')), manifest.archives['owned-run.zip']);
    const root = mkdtempSync(join(tmpdir(), 'nt-osc633-impl-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        const frames = readFileSync(join(root, 'vs633-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
        const bytes = readFileSync(join(root, 'vs633-observations.json'));
        const measured = JSON.parse(bytes);
        const rebuilt = buildOsc633GradingReport(frames, JSON.parse(readFileSync(join(root, 'vs633-run.json'))), measured.runId, measured.adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), bytes), 0);
        const projected = osc633ImplementationPublication(root);
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.equal(schemaVersion, 69); assert.equal(fixtureRemoved, true);
        assert.equal(frames.length, 63);
        assert.equal(projected.summary.appRevision, measuredAppRevision);
        assert.equal(projected.summary.scriptsRevision, measuredScriptsRevision);
        assert.equal(projected.summary.context.runId, '323882a7d7b0e3c86ce4152fa54e6aa3');
        assert.equal(projected.summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v62');
        assert.deepEqual(original.collectionScope.omittedSuites, ['osc9', 'notify']);
        assert.equal(original.collectionScope.predecessor.runId, 'b45702ad11ea1f0ea46eeb4908206155');
        assert.deepEqual(original.osc633Evidence.counts, { 'inconclusive/supported': 4 });
        assert.deepEqual(projected.report.cases.map(item => item.caseId), [...osc633CaseIds]);
        for (const item of projected.report.cases) {
            assert.equal(item.pinned.termless.observation.outcome, 'inconclusive');
            assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive');
            assert.ok(item.effectContract.validity.every(value => value.held));
            assert.equal(item.contractResult.outcome, 'supported');
            assert.equal(item.divergence.kind, 'pinned-inconclusive-by-design');
            assert.equal(item.stock.outcome, 'inconclusive');
        }
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('the remeasurement is bound to a changed App and the same pinned bundle and effect contract as the retained unsupported baseline', () => {
    const baseline = JSON.parse(readFileSync(join(baselineDir, 'summary.json')));
    const baselineReport = JSON.parse(readFileSync(join(baselineDir, 'report.json')));
    const current = JSON.parse(read('summary.json')), report = JSON.parse(read('report.json'));
    // A different built App (assembly) and a different run; the measured contract and the pinned adapter are the same.
    assert.notEqual(current.context.assemblySha256, baseline.context.assemblySha256);
    assert.notEqual(current.ownedRunId, baseline.ownedRunId);
    assert.equal(report.suiteId, baselineReport.suiteId);
    assert.equal(report.contractsVersion, baselineReport.contractsVersion);
    // The derived collector bundle is regenerated per run (its digest is not a stable identity); the pinned upstream bundle and adapter lineage are.
    assert.equal(report.adapter.id, baselineReport.adapter.id);
    assert.equal(report.adapter.derivedFromAdapterId, baselineReport.adapter.derivedFromAdapterId);
    assert.equal(report.adapter.upstreamBundleSha256, baselineReport.adapter.upstreamBundleSha256);
    assert.deepEqual(report.cases.map(item => item.effectContract.id), baselineReport.cases.map(item => item.effectContract.id));
    for (const [index, item] of report.cases.entries()) {
        // The baseline's historical negative stays unsupported; only the remeasured App's outcome is supported.
        assert.equal(baselineReport.cases[index].contractResult.outcome, 'unsupported');
        assert.equal(item.contractResult.outcome, 'supported');
    }
});

test('the remeasurement makes no command exit-status claim from the child process exit code', () => {
    const report = JSON.parse(read('report.json'));
    const dCase = report.cases.find(item => item.caseId === 'extensions.osc633-d');
    for (const name of ['no-exit-code', 'nonzero-exit-code']) {
        const experiment = dCase.experiments.find(item => item.name === name);
        assert.ok(experiment, name);
        for (const read of experiment.reads) {
            for (const value of Object.values(read)) {
                if (value?.observed) assert.equal(value.observed.exitCode, null);
            }
        }
    }
});
