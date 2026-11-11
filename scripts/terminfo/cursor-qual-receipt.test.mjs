import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture } from './fixtures/private-clipboard-receipt.mjs';
import { includesCursorQualEvidence, checkCursorQualEvidence } from './cursor-qual-receipt.mjs';
import { cursorQualAdapterId } from './cursor-qual-grading-collector.mjs';
import { cursorQualSuiteId } from './cursor-qual-contracts.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const pngs = f => readFileSync(join(f.output, 'curq-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)).filter(frame => frame.pixel).map(frame => frame.pixel.file);
/** Re-bind a changed retained file so only the semantic change is under test, not the stale digest. */
const rebind = (f, field, file) => { f.evidence.cursorQualEvidence[field] = hash(readFileSync(join(f.output, file))); };


/** This layer's own receipt check over the fixture's current files and declarations. The complete-run test and the changed-report
 * test go through the whole receipt; the refusals below call the layer check directly, so each one costs this layer's rederivation
 * instead of every layer's, and a refusal must be a deliberate one, never a TypeError from a missing object. */
const own = f => () => checkCursorQualEvidence(f.output, f.evidence, f.context.runId, f.context);
const refusal = error => !(error instanceof TypeError);
test('a complete run carries the cursor suffix: counts, adapter identity and every retained PNG are rederived without the collector', () => privateClipboardReceiptFixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.equal(f.finish().schemaVersion, 51);
    assert.deepEqual(f.evidence.cursorQualEvidence.counts, { supported: 4 });
    assert.equal(f.context.cursorQualAdapterId, cursorQualAdapterId);
    assert.equal(f.context.cursorQualSuiteId, cursorQualSuiteId);
    assert.ok(pngs(f).length >= 10 && pngs(f).every(file => existsSync(join(f.output, file))));
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => privateClipboardReceiptFixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v27' };
    assert.equal(includesCursorQualEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesCursorQualEvidence(empty, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v28' }), true, 'the current policy always carries the suffix');
    assert.equal(includesCursorQualEvidence(empty, {}, earlier), false, 'an earlier policy without any artifact or declaration does not');
    assert.equal(includesCursorQualEvidence(empty, {}, { ...earlier, cursorQualSuiteId: 'x' }), true);
    assert.equal(includesCursorQualEvidence(empty, { cursorQualEvidence: {} }, earlier), true);
    assert.equal(includesCursorQualEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.cursorQualEvidence; }],
    ['changed suite identity', f => { f.evidence.cursorQualEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.cursorQualEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.cursorQualEvidence.counts = { supported: 3, inconclusive: 1 }; }],
    ['a stale observation digest', f => { f.evidence.cursorQualEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.cursorQualEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.cursorQualEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.cursorQualEvidence.contractsVersion = 2; }],
    ['a changed adapter declaration', f => { f.context.cursorQualAdapterId = 'other'; }],
    ['a changed adapter bundle declaration', f => { f.context.cursorQualBundleSha256 = '0'.repeat(64); }],
    ['a missing attribute-rectangle predecessor', f => { delete f.evidence.attrRectEvidence; }],
    ['a changed predecessor bundle', f => { f.context.attrRectBundleSha256 = '0'.repeat(64); }],
]) test('the cursor receipt refuses ' + label, () => privateClipboardReceiptFixture(f => { mutate(f); f.write('context.json', f.context); assert.throws(own(f), refusal); }));

test('the cursor receipt refuses an adapter that is not the retained layered derivation', () => privateClipboardReceiptFixture(f => {
    writeFileSync(join(f.output, 'collector-curq.mjs'), readFileSync(join(f.output, 'collector-curq.mjs'), 'utf8') + '\n// edited\n');
    rebind(f, 'adapterBundleSha256', 'collector-curq.mjs');
    f.context.cursorQualBundleSha256 = f.evidence.cursorQualEvidence.adapterBundleSha256; f.write('context.json', f.context);
    assert.throws(own(f), /not the retained layered derivation/);
}));

test('the cursor receipt refuses a changed journal, run log or report even when the binding is rehashed to match', () => privateClipboardReceiptFixture(f => {
    const run = JSON.parse(readFileSync(join(f.output, 'curq-run.json')));
    run.cases[3].pinned.original.threw.name = 'Error';
    writeFileSync(join(f.output, 'curq-run.json'), JSON.stringify(run, null, 2) + '\n'); rebind(f, 'runSha256', 'curq-run.json');
    assert.throws(own(f), /original cursor.reverse-wrap failure was not retained|do not match retained evidence/);
}));

test('the cursor receipt refuses a changed report that no longer equals its rederivation', () => privateClipboardReceiptFixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'curq-observations.json')));
    report.cases[0].conclusion.outcome = 'inconclusive'; report.cases[0].contractResult.outcome = 'inconclusive';
    writeFileSync(join(f.output, 'curq-observations.json'), JSON.stringify(report, null, 2) + '\n'); rebind(f, 'sha256', 'curq-observations.json');
    f.evidence.cursorQualEvidence.counts = { supported: 3, inconclusive: 1 };
    assert.throws(f.finish, /observations do not match retained evidence/);
}));

test('the cursor receipt refuses a truncated journal and a missing retained artifact', () => privateClipboardReceiptFixture(f => {
    const journal = join(f.output, 'curq-snapshots.jsonl'), lines = readFileSync(journal, 'utf8').trimEnd().split('\n');
    writeFileSync(journal, lines.slice(0, -1).join('\n') + '\n');
    assert.throws(own(f), refusal);
    writeFileSync(journal, lines.join('\n') + '\n');
    rmSync(join(f.output, 'curq-run.json'));
    assert.throws(own(f), refusal);
}));

test('the cursor receipt refuses a retained PNG that was edited, removed or replaced by a symlink', () => privateClipboardReceiptFixture(f => {
    const [file] = pngs(f), path = join(f.output, file), original = readFileSync(path);
    const bytes = Buffer.from(original); bytes[bytes.length - 20] ^= 1; writeFileSync(path, bytes);
    assert.throws(own(f), /controlled pixel capture/);
    rmSync(path);
    assert.throws(own(f), refusal);
    writeFileSync(join(f.output, 'elsewhere.png'), original);
    symlinkSync(join(f.output, 'elsewhere.png'), path);
    assert.throws(own(f), /controlled pixel capture/);
}));
