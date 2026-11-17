import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { checkOsc720Evidence } from './osc720-receipt.mjs';
import { osc22Publication } from './osc22-publication.mjs';
import { buildOsc22GradingReport } from './osc22-grading.mjs';
import { osc22Baseline, osc22CaseIds } from './osc22-contracts.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-17-osc22/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));
const json = (root, name) => JSON.parse(readFileSync(join(root, name)));
function manifest() {
    assert.equal(hash(read('manifest.json')), '265743a1894e63fb76de394879953a7a13ccbbba4542b514957b31fbc26ed6d3');
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
        assert.equal(original.runId, '412aa71eeb2f93be09465d03b607eb56');
        const frames = readFileSync(join(root, 'ptr-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
        const bytes = readFileSync(join(root, 'ptr-observations.json')), measured = JSON.parse(bytes);
        const rebuilt = buildOsc22GradingReport(frames, json(root, 'ptr-run.json'), measured.runId, measured.adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), bytes), 0);
        const projected = osc22Publication(root); projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.equal(projected.summary.appRevision, '2e8ef66c8c6ee86758c75538ac57dd42d623c52d');
        assert.equal(projected.summary.scriptsRevision, '750f137192ae07588c1e63f87073d5b297a972b3');
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

test('the failed dispatch attempt and all five lease releases remain retained without admitting the failed run', () => {
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
        for (const [file, id] of [['attempt-1.log', context.runId], ['attempt-2.log', 'a188ef080019680287f85cab559f5adf'], ['attempt-3.log', '95db74fe99675694c4197ccfca70df84'], ['attempt-4.log', '72b465974ad2e13e4b0a1113444f8063'], ['attempt-5.log', pins.runId]]) {
            assert.ok(readFileSync(join(logs, file), 'utf8').includes('desktop lease ' + id + ' released after verified cleanup.'));
        }
        assert.deepEqual(JSON.parse(read('attempts.json')).attempts.map(item => item.disposition), ['failed-not-admitted', 'superseded-pre-hover-fix', 'superseded-pre-corrected-predecessor', 'failed-not-admitted', 'admitted']);
    } finally { rmSync(root, { recursive: true, force: true }); rmSync(logs, { recursive: true, force: true }); }
});

for (const [attempt, name, runId, appRevision, scriptsRevision] of [
    [2, 'superseded/attempt-2-pre-hover-fix.zip', 'a188ef080019680287f85cab559f5adf', '3096b8609ce1fe1bfbbb4386a54653443433a409', '3c139127c367919458713b29e641c91c859fb831'],
    [3, 'superseded/attempt-3-before-corrected-predecessor.zip', '95db74fe99675694c4197ccfca70df84', 'bcc85a1fdadcd88d753272e46d273fbe4bb4bb3e', 'bcc85a1fdadcd88d753272e46d273fbe4bb4bb3e'],
]) test(`historical attempt ${attempt} retains its OSC22 report but cannot pass the corrected OSC720 receipt plan`, () => {
    const pins = manifest(), root = mkdtempSync(join(tmpdir(), 'nt-osc22-prior-app-'));
    try {
        extractOwnedArchive(dir, { archives: { 'owned-run.zip': pins.archives[name] } }, root, join(dir, name));
        const receipt = json(root, 'cell-receipt.json'), proof = json(root, 'ownership-proof.json');
        assert.equal(receipt.runId, runId);
        assert.equal(receipt.fixtureRemoved, true); assert.equal(proof.completed, true); assert.ok(proof.cleanedUpAt); assert.equal(proof.cleanupError, undefined);
        const frames = readFileSync(join(root, 'ptr-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
        const measured = json(root, 'ptr-observations.json');
        assert.deepEqual(buildOsc22GradingReport(frames, json(root, 'ptr-run.json'), measured.runId, measured.adapter), measured);
        const projected = osc22Publication(root, { appRevision, scriptsRevision });
        assert.deepEqual(projected.summary, JSON.parse(read(`superseded/attempt-${attempt}-summary.json`)));
        assert.deepEqual(projected.report, JSON.parse(read(`superseded/attempt-${attempt}-report.json`)));
        assert.deepEqual(receipt.osc22Evidence.counts, { 'inconclusive/supported': 1 });
        assert.notEqual(receipt.runId, pins.runId);
        assert.throws(() => checkOsc720Evidence(root, receipt, runId, json(root, 'context.json')), /Incomplete OSC 720 journal/);
    } finally { rmSync(root, { recursive: true, force: true }); }
});


test('animation RPC failure retains its complete raw seal and pointer journal without admitting either effect', () => {
    const pins = manifest(), root = mkdtempSync(join(tmpdir(), 'nt-osc22-animation-failed-'));
    try {
        const name = 'superseded/attempt-4-animation-rpc-failure.zip';
        extractOwnedArchive(dir, { archives: { 'owned-run.zip': pins.archives[name] } }, root, join(dir, name));
        const context = json(root, 'context.json'), proof = json(root, 'ownership-proof.json');
        assert.equal(context.runId, '72b465974ad2e13e4b0a1113444f8063');
        assert.equal(proof.completed, false); assert.ok(proof.cleanedUpAt); assert.equal(proof.cleanupError, undefined);
        assert.equal(json(root, 'failure.json').error, 'Ntilde owner did not respond.');
        assert.equal(existsSync(join(root, 'cell-receipt.json')), false);
        assert.equal(existsSync(join(root, 'ptr-observations.json')), false);
        assert.equal(readFileSync(join(root, 'ptr-snapshots.jsonl'), 'utf8').trim().split('\n').length, 18);
        assert.equal(readFileSync(join(root, 'animation-snapshots.jsonl'), 'utf8').trim().split('\n').length, 31);
        const seal = proof.proofs[0].kittyAnimation.seal;
        assert.equal(seal.failed, false); assert.equal(seal.disposed, true);
        assert.equal(seal.attempts, 30); assert.equal(seal.lastAttempt.completed, true);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
