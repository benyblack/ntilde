import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { osc633Publication } from './osc633-publication.mjs';
import { buildOsc633GradingReport } from './osc633-grading.mjs';
import { osc633CaseIds } from './osc633-grading-collector.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-16-osc633/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('retained OSC 633 run independently rederives the receipt, paired journal, and publication', () => {
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), '9fe4f267918c2776fa5376fb02f0a382ed9648dd7fd33078a66c6ab42dfe70a3');
    const manifest = JSON.parse(manifestBytes);
    assert.deepEqual(Object.keys(manifest.archives), ['owned-run.zip', 'superseded/attempt-1-deadline.zip', 'superseded/attempt-2-scoped.zip', 'superseded/default-validation-interrupted.zip', 'superseded/default-validation-interrupted-2.zip', 'default-validation/owned-run.zip']);
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-osc633-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        const frames = readFileSync(join(root, 'vs633-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
        const bytes = readFileSync(join(root, 'vs633-observations.json'));
        const measured = JSON.parse(bytes), rebuilt = buildOsc633GradingReport(frames, JSON.parse(readFileSync(join(root, 'vs633-run.json'))), measured.runId, measured.adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), bytes), 0);
        const projected = osc633Publication(root);
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.equal(schemaVersion, 68); assert.equal(fixtureRemoved, true);
        assert.deepEqual(original.collectionScope.omittedSuites, ['osc9', 'notify']);
        assert.equal(original.collectionScope.predecessor.runId, '55f0ba462cd3cc6fbc315a0e5fad5126');
        assert.equal(projected.summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v61');
        assert.deepEqual(projected.report.cases.map(item => item.caseId), [...osc633CaseIds]);
        assert.equal(frames.length, 63);
        for (const item of projected.report.cases) {
            assert.equal(item.pinned.termless.observation.outcome, 'inconclusive');
            assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive');
            assert.ok(item.effectContract.validity.every(value => value.held));
            assert.equal(item.contractResult.outcome, 'unsupported');
            assert.equal(item.stock.outcome, 'inconclusive');
        }
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('superseded and interrupted attempts retain their distinct cleanup and outcome states', () => {
    const manifest = JSON.parse(read('manifest.json'));
    for (const [name, runId, completed] of [
        ['superseded/attempt-1-deadline.zip', 'e4fd81d6c7bb7e0d0557276f1eb740c1', false],
        ['superseded/attempt-2-scoped.zip', '5040e7f86d90b51061a9166d45c1750a', true],
        ['superseded/default-validation-interrupted.zip', '8efbd0971a5f59f2a27d059e36dc496a', false],
        ['superseded/default-validation-interrupted-2.zip', '5d26045795c25b3c969b7d49d43bae8a', false],
    ]) {
        const root = mkdtempSync(join(tmpdir(), 'nt-osc633-attempt-'));
        try {
            extractOwnedArchive(dir, { archives: { 'owned-run.zip': manifest.archives[name] } }, root, join(dir, name));
            const context = JSON.parse(readFileSync(join(root, 'context.json')));
            const proof = JSON.parse(readFileSync(join(root, 'ownership-proof.json')));
            assert.equal(context.runId, runId); assert.equal(proof.completed, completed);
            assert.ok(proof.cleanedUpAt); assert.equal(proof.cleanupError, undefined);
            assert.equal(existsSync(join(root, 'cell-receipt.json')), completed);
            if (completed) {
                const receipt = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
                assert.equal(receipt.runId, runId); assert.equal(receipt.fixtureRemoved, true);
                assert.deepEqual(receipt.osc633Evidence.counts, { 'inconclusive/unsupported': 4 });
            }
        } finally { rmSync(root, { recursive: true, force: true }); }
    }
});

test('default selection independently rederives both notification suites without claiming OSC 633', () => {
    const manifest = JSON.parse(read('manifest.json')), name = 'default-validation/owned-run.zip';
    const root = mkdtempSync(join(tmpdir(), 'nt-osc633-default-'));
    try {
        extractOwnedArchive(dir, { archives: { 'owned-run.zip': manifest.archives[name] } }, root, join(dir, name));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        assert.equal(schemaVersion, 68); assert.equal(fixtureRemoved, true);
        assert.equal(original.runId, '6d2c799d710b798c77ab75f76d9e3017');
        assert.deepEqual(original.collectionScope.omittedSuites, ['osc633']);
        assert.equal(original.osc633Evidence, undefined);
        assert.deepEqual(original.osc9Evidence.counts, { 'inconclusive/supported': 2 });
        assert.deepEqual(original.notifyEvidence.counts, { 'inconclusive/supported': 1, 'stock-and-query-witness/supported': 1 });
        rmSync(join(root, 'cell-receipt.json'));
        const rederived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...rederived, completedAt }, original);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
