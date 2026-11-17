import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { osc22Publication } from './osc22-publication.mjs';
import { buildOsc22GradingReport } from './osc22-grading.mjs';
import { osc22Baseline, osc22CaseIds } from './osc22-contracts.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-17-osc22/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));
const json = (root, name) => JSON.parse(readFileSync(join(root, name)));
function manifest() {
    assert.equal(hash(read('manifest.json')), 'f24ad1c4b1db7e251cbce7e04c507803001ab050264a3bfd91e86dc847794d10');
    const value = JSON.parse(read('manifest.json'));
    for (const [name, digest] of Object.entries({ ...value.archives, ...value.files })) assert.equal(hash(read(name)), digest, name);
    return value;
}

test('the retained OSC22 measurement independently rederives its complete receipt and publication', () => {
    const pins = manifest(), root = mkdtempSync(join(tmpdir(), 'nt-osc22-publication-'));
    try {
        extractOwnedArchive(dir, pins, root, join(dir, 'owned-run.zip'));
        const original = json(root, 'cell-receipt.json');
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        assert.equal(schemaVersion, 75); assert.equal(fixtureRemoved, true);
        assert.equal(original.runId, '95db74fe99675694c4197ccfca70df84');
        const frames = readFileSync(join(root, 'ptr-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
        const bytes = readFileSync(join(root, 'ptr-observations.json')), measured = JSON.parse(bytes);
        const rebuilt = buildOsc22GradingReport(frames, json(root, 'ptr-run.json'), measured.runId, measured.adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), bytes), 0);
        const projected = osc22Publication(root); projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.equal(projected.summary.appRevision, 'bcc85a1fdadcd88d753272e46d273fbe4bb4bb3e');
        assert.equal(projected.summary.scriptsRevision, 'bcc85a1fdadcd88d753272e46d273fbe4bb4bb3e');
        assert.equal(projected.summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v68');
        assert.equal(Object.keys(json(root, 'source-snapshot.json').hashes).length, 455);
        assert.deepEqual(original.collectionScope.omittedSuites, ['osc9', 'notify']);
        assert.deepEqual(original.osc22Evidence.counts, { 'inconclusive/supported': 1 });
        assert.equal(frames.length, 18);
        assert.deepEqual(frames[1].before.metadata.pointerShape, osc22Baseline);
        assert.deepEqual(frames.at(-1).before.metadata.pointerShape, osc22Baseline);
        assert.deepEqual(projected.report.cases.map(item => item.caseId), [...osc22CaseIds]);
        const [item] = projected.report.cases;
        assert.equal(item.pinned.termlessFunction, false); assert.equal(item.pinned.termless, null);
        assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive');
        assert.equal(item.pinned.terminal.response, '1;1');
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.equal(item.effectContract.outcome, 'supported');
        assert.equal(item.effectContract.required.length, 8); assert.ok(item.effectContract.required.every(value => value.held));
        assert.equal(item.effectContract.validity.length, 9); assert.ok(item.effectContract.validity.every(value => value.held));
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
        assert.equal(item.stock.outcome, 'inconclusive');
        assert.deepEqual(projected.summary.stockCollector.counts, { inconclusive: 160, supported: 94, unsupported: 1 });
        for (const [name, requested, cursor] of [['stock', 'pointer', 'Hand'], ['delivery-text', 'text', 'Ibeam'], ['crosshair', 'crosshair', 'Cross'], ['default', 'default', 'Arrow'], ['reset', null, 'Arrow']]) {
            assert.deepEqual(item.experiments.find(value => value.name === name).reads[0].pointer, { requested, controlCursor: cursor, linkOverride: false });
        }
        assert.equal(json(root, 'ownership-proof.json').completed, true);
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('the failed dispatch attempt and all three lease releases remain retained without admitting the failed run', () => {
    const pins = manifest(), root = mkdtempSync(join(tmpdir(), 'nt-osc22-failed-')), logs = mkdtempSync(join(tmpdir(), 'nt-osc22-logs-'));
    try {
        const name = 'superseded/attempt-1.zip';
        extractOwnedArchive(dir, { archives: { 'owned-run.zip': pins.archives[name] } }, root, join(dir, name));
        const context = json(root, 'context.json'), proof = json(root, 'ownership-proof.json');
        assert.equal(context.runId, '6a6b07d5362038927fc8eb5c06bd2871');
        assert.equal(proof.completed, false); assert.ok(proof.cleanedUpAt); assert.equal(proof.cleanupError, undefined);
        assert.equal(json(root, 'failure.json').error, 'Unknown owner request.');
        for (const file of ['cell-receipt.json', 'ptr-run.json', 'ptr-observations.json']) assert.equal(existsSync(join(root, file)), false);
        extractOwnedArchive(dir, { archives: { 'owned-run.zip': pins.archives['collection-logs.zip'] } }, logs, join(dir, 'collection-logs.zip'));
        for (const [file, id] of [['attempt-1.log', context.runId], ['attempt-2.log', 'a188ef080019680287f85cab559f5adf'], ['attempt-3.log', pins.runId]]) {
            assert.ok(readFileSync(join(logs, file), 'utf8').includes('desktop lease ' + id + ' released after verified cleanup.'));
        }
        assert.deepEqual(JSON.parse(read('attempts.json')).attempts.map(item => item.disposition), ['failed-not-admitted', 'superseded-pre-hover-fix', 'admitted']);
    } finally { rmSync(root, { recursive: true, force: true }); rmSync(logs, { recursive: true, force: true }); }
});

test('the pre-hover-fix App measurement retains its own journal, verdict, publication and cleanup', () => {
    const pins = manifest(), root = mkdtempSync(join(tmpdir(), 'nt-osc22-prior-app-'));
    try {
        const name = 'superseded/attempt-2-pre-hover-fix.zip';
        extractOwnedArchive(dir, { archives: { 'owned-run.zip': pins.archives[name] } }, root, join(dir, name));
        const receipt = json(root, 'cell-receipt.json'), proof = json(root, 'ownership-proof.json');
        assert.equal(receipt.runId, 'a188ef080019680287f85cab559f5adf');
        assert.equal(receipt.fixtureRemoved, true); assert.equal(proof.completed, true); assert.ok(proof.cleanedUpAt); assert.equal(proof.cleanupError, undefined);
        const frames = readFileSync(join(root, 'ptr-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
        const measured = json(root, 'ptr-observations.json');
        assert.deepEqual(buildOsc22GradingReport(frames, json(root, 'ptr-run.json'), measured.runId, measured.adapter), measured);
        const projected = osc22Publication(root, { appRevision: '3096b8609ce1fe1bfbbb4386a54653443433a409', scriptsRevision: '3c139127c367919458713b29e641c91c859fb831' });
        assert.deepEqual(projected.summary, JSON.parse(read('superseded/attempt-2-summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('superseded/attempt-2-report.json')));
        assert.deepEqual(receipt.osc22Evidence.counts, { 'inconclusive/supported': 1 });
        assert.notEqual(receipt.runId, pins.runId);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
