import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture } from './fixtures/private-clipboard-receipt.mjs';
import { includesColEditEvidence, checkColEditEvidence } from './col-edit-receipt.mjs';
import { colEditAdapterId } from './col-edit-grading-collector.mjs';
import { colEditSuiteId } from './col-edit-contracts.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Re-bind a changed retained file so only the semantic change is under test, not the stale digest. */
const rebind = (f, field, file) => { f.evidence.colEditEvidence[field] = hash(readFileSync(join(f.output, file))); };


/** This layer's own receipt check over the fixture's current files and declarations. The complete-run test and the changed-report
 * test go through the whole receipt; the refusals below call the layer check directly, so each one costs this layer's rederivation
 * instead of every layer's, and a refusal must be a deliberate one, never a TypeError from a missing object. */
const own = f => () => checkColEditEvidence(f.output, f.evidence, f.context.runId, f.context);
const refusal = error => !(error instanceof TypeError);
test('a complete run carries the column-editing suffix: counts, adapter identity and the layered derivation are rederived without the collector', () => privateClipboardReceiptFixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.equal(f.finish().schemaVersion, 51);
    assert.deepEqual(f.evidence.colEditEvidence.counts, { supported: 4 });
    assert.equal(f.context.colEditAdapterId, colEditAdapterId);
    assert.equal(f.context.colEditSuiteId, colEditSuiteId);
    // The retained suffix sits beside the rectangle-editing suffix, never in place of it.
    assert.ok(f.evidence.rectEditEvidence && f.evidence.selectiveEraseEvidence && f.evidence.eraseCharsHistoryEvidence && f.evidence.cursorQualEvidence && f.evidence.attrRectEvidence && f.evidence.eraseAttrsRegionEvidence);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => privateClipboardReceiptFixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v31' };
    assert.equal(includesColEditEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesColEditEvidence(empty, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v32' }), true, 'the current policy always carries the suffix');
    assert.equal(includesColEditEvidence(empty, {}, earlier), false, 'an earlier policy without any artifact or declaration does not');
    assert.equal(includesColEditEvidence(empty, {}, { ...earlier, colEditSuiteId: 'x' }), true);
    assert.equal(includesColEditEvidence(empty, { colEditEvidence: {} }, earlier), true);
    assert.equal(includesColEditEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.colEditEvidence; }],
    ['changed suite identity', f => { f.evidence.colEditEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.colEditEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.colEditEvidence.counts = { supported: 3, inconclusive: 1 }; }],
    ['a stale observation digest', f => { f.evidence.colEditEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.colEditEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.colEditEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.colEditEvidence.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.colEditContractsVersion = 2; }],
    ['a missing adapter declaration', f => { delete f.context.colEditAdapterId; }],
    ['a missing suite declaration', f => { delete f.context.colEditSuiteId; }],
    ['a changed adapter declaration', f => { f.context.colEditAdapterId = 'other'; }],
    ['a changed adapter bundle declaration', f => { f.context.colEditBundleSha256 = '0'.repeat(64); }],
    ['a missing rectangle-editing predecessor', f => { delete f.evidence.rectEditEvidence; }],
    ['a missing rectangle-editing predecessor declaration', f => { delete f.context.rectEditAdapterId; }],
    ['a changed rectangle-editing predecessor bundle', f => { f.context.rectEditBundleSha256 = '0'.repeat(64); }],
    ['a missing selective-erasure predecessor', f => { delete f.evidence.selectiveEraseEvidence; }],
    ['a changed selective-erasure predecessor bundle', f => { f.context.selectiveEraseBundleSha256 = '0'.repeat(64); }],
    ['a missing erase-character/saved-lines predecessor', f => { delete f.evidence.eraseCharsHistoryEvidence; }],
    ['a changed erase-character/saved-lines predecessor bundle', f => { f.context.eraseCharsHistoryBundleSha256 = '0'.repeat(64); }],
    ['a missing cursor predecessor', f => { delete f.evidence.cursorQualEvidence; }],
    ['a missing cursor predecessor declaration', f => { delete f.context.cursorQualAdapterId; }],
    ['a changed cursor predecessor bundle', f => { f.context.cursorQualBundleSha256 = '0'.repeat(64); }],
    ['a missing attribute-rectangle predecessor', f => { delete f.evidence.attrRectEvidence; }],
    ['a changed attribute-rectangle predecessor bundle', f => { f.context.attrRectBundleSha256 = '0'.repeat(64); }],
    ['a missing erase attributes/margins predecessor', f => { delete f.evidence.eraseAttrsRegionEvidence; }],
    ['a changed erase attributes/margins predecessor bundle', f => { f.context.eraseAttrsRegionBundleSha256 = '0'.repeat(64); }],
    ['a missing saved-state/reset predecessor', f => { delete f.evidence.savedResetEvidence; }],
    ['a changed saved-state/reset predecessor bundle', f => { f.context.savedResetBundleSha256 = '0'.repeat(64); }],
    ['a missing scrolling/history predecessor', f => { delete f.evidence.scrollbackEvidence; }],
]) test('the column-editing receipt refuses ' + label, () => privateClipboardReceiptFixture(f => { mutate(f); f.write('context.json', f.context); assert.throws(own(f), refusal); }));

test('the column-editing receipt refuses an adapter that is not the retained layered derivation', () => privateClipboardReceiptFixture(f => {
    writeFileSync(join(f.output, 'collector-cole.mjs'), readFileSync(join(f.output, 'collector-cole.mjs'), 'utf8') + '\n// edited\n');
    rebind(f, 'adapterBundleSha256', 'collector-cole.mjs');
    f.context.colEditBundleSha256 = f.evidence.colEditEvidence.adapterBundleSha256; f.write('context.json', f.context);
    assert.throws(own(f), /not the retained layered derivation/);
}));

test('the column-editing receipt refuses a changed run log even when the binding is rehashed to match', () => privateClipboardReceiptFixture(f => {
    const run = JSON.parse(readFileSync(join(f.output, 'cole-run.json')));
    run.cases[0].reads = run.cases[0].reads.slice(1);
    writeFileSync(join(f.output, 'cole-run.json'), JSON.stringify(run, null, 2) + '\n'); rebind(f, 'runSha256', 'cole-run.json');
    assert.throws(own(f), refusal);
}));

test('the column-editing receipt refuses a changed report that no longer equals its rederivation', () => privateClipboardReceiptFixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'cole-observations.json')));
    report.cases[0].conclusion.outcome = 'inconclusive'; report.cases[0].contractResult.outcome = 'inconclusive';
    writeFileSync(join(f.output, 'cole-observations.json'), JSON.stringify(report, null, 2) + '\n'); rebind(f, 'sha256', 'cole-observations.json');
    f.evidence.colEditEvidence.counts = { supported: 3, inconclusive: 1 };
    assert.throws(f.finish, /observations do not match retained evidence/);
}));

test('the column-editing receipt refuses a truncated journal, an empty journal and a missing retained artifact', () => privateClipboardReceiptFixture(f => {
    const journal = join(f.output, 'cole-snapshots.jsonl'), lines = readFileSync(journal, 'utf8').trimEnd().split('\n');
    writeFileSync(journal, lines.slice(0, -1).join('\n') + '\n');
    assert.throws(own(f), refusal);
    writeFileSync(journal, '\n');
    assert.throws(own(f), /journal is empty/);
    writeFileSync(journal, lines.join('\n') + '\n');
    rmSync(join(f.output, 'cole-run.json'));
    assert.throws(own(f), refusal);
}));
