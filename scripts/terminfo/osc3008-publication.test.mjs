import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { osc3008Publication } from './osc3008-publication.mjs';
import { buildOsc3008GradingReport } from './osc3008-grading.mjs';
import { osc3008CaseIds } from './osc3008-contracts.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-17-osc3008/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('retained OSC 3008 run independently rederives the receipt, paired journal and publication', () => {
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), 'd6ad75e5ecef35d26d5f1bcaa3b0451a45b8c22d32b4f87d34ee6956dc635a8d');
    const manifest = JSON.parse(manifestBytes);
    assert.deepEqual(Object.keys(manifest.archives), ['owned-run.zip', 'superseded/attempt-1-pre-merge.zip']);
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-osc3008-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        const frames = readFileSync(join(root, 'ocx-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
        const bytes = readFileSync(join(root, 'ocx-observations.json'));
        const measured = JSON.parse(bytes), rebuilt = buildOsc3008GradingReport(frames, JSON.parse(readFileSync(join(root, 'ocx-run.json'))), measured.runId, measured.adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), bytes), 0);
        const projected = osc3008Publication(root);
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.equal(schemaVersion, 73); assert.equal(fixtureRemoved, true);
        assert.equal(original.runId, '34dd71da0b96927426033d07458e20de');
        assert.equal(projected.summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v66');
        assert.deepEqual(original.collectionScope.omittedSuites, ['osc9', 'notify']);
        assert.deepEqual(original.osc3008Evidence.counts, { 'inconclusive/supported': 1 });
        assert.deepEqual(projected.report.cases.map(item => item.caseId), [...osc3008CaseIds]);
        assert.equal(frames.length, 41);
        const [item] = projected.report.cases;
        assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive');
        assert.equal(item.pinned.termless, null);
        assert.equal(item.pinned.observation.outcome, 'inconclusive');
        assert.equal(item.pinned.terminal.response, '1;1');
        assert.deepEqual(item.conclusion, { outcome: 'inconclusive', grading: 'pinned-terminal-unmeasured-preserved-by-design', evidence: 'owned-pane-metadata-and-session-status' });
        assert.ok(item.effectContract.validity.every(value => value.held));
        assert.equal(item.effectContract.required.length, 28);
        assert.ok(item.effectContract.required.every(value => value.held));
        assert.ok(item.effectContract.policy.every(value => value.match));
        assert.equal(item.contractResult.outcome, 'supported');
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
        assert.equal(item.stock.outcome, 'inconclusive');
        // Every observed context was reported as untrusted data, and the run ended with an empty stack (the ended ring is not restorable).
        assert.ok(frames.every(frame => frame.before.metadata.buffer.contexts.untrusted === true));
        assert.deepEqual(frames.at(-1).before.metadata.buffer.contexts.stack, []);
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('the superseded pre-merge run is retained with its own cleanup and outcome', () => {
    const manifest = JSON.parse(read('manifest.json')), name = 'superseded/attempt-1-pre-merge.zip';
    const root = mkdtempSync(join(tmpdir(), 'nt-osc3008-superseded-'));
    try {
        extractOwnedArchive(dir, { archives: { 'owned-run.zip': manifest.archives[name] } }, root, join(dir, name));
        const receipt = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const proof = JSON.parse(readFileSync(join(root, 'ownership-proof.json')));
        assert.equal(receipt.runId, 'e8e16d88461607069d1dbae74441d4ee'); assert.equal(receipt.fixtureRemoved, true);
        assert.equal(proof.completed, true); assert.ok(proof.cleanedUpAt); assert.equal(proof.cleanupError, undefined);
        assert.deepEqual(receipt.osc3008Evidence.counts, { 'inconclusive/supported': 1 });
        // The two runs are different observations of the same contract on different Apps: neither stands in for the other.
        assert.notEqual(receipt.runId, JSON.parse(readFileSync(join(dir, 'summary.json'))).ownedRunId);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
