import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture } from './fixtures/private-clipboard-receipt.mjs';
import { includesRectChecksumEvidence, checkRectChecksumEvidence } from './rect-checksum-receipt.mjs';
import { rectChecksumAdapterId } from './rect-checksum-grading-collector.mjs';
import { rectChecksumSuiteId } from './rect-checksum-contracts.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Re-bind a changed retained file so only the semantic change is under test, not the stale digest. */
const rebind = (f, field, file) => { f.evidence.rectChecksumEvidence[field] = hash(readFileSync(join(f.output, file))); };


/** This layer's own receipt check over the fixture's current files and declarations. The complete-run test and the changed-report
 * test go through the whole receipt; the refusals below call the layer check directly, so each one costs this layer's rederivation
 * instead of every layer's, and a refusal must be a deliberate one, never a TypeError from a missing object. */
const own = f => () => checkRectChecksumEvidence(f.output, f.evidence, f.context.runId, f.context);
const refusal = error => !(error instanceof TypeError);
test('a complete run carries the rectangle-checksum suffix: counts, adapter identity and the layered derivation are rederived without the collector', () => privateClipboardReceiptFixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.equal(f.finish().schemaVersion, 39);
    assert.deepEqual(f.evidence.rectChecksumEvidence.counts, { supported: 1 });
    assert.equal(f.context.rectChecksumAdapterId, rectChecksumAdapterId);
    assert.equal(f.context.rectChecksumSuiteId, rectChecksumSuiteId);
    // The retained suffix sits beside the column-editing suffix, never in place of it.
    assert.ok(f.evidence.colEditEvidence && f.evidence.rectEditEvidence && f.evidence.selectiveEraseEvidence && f.evidence.eraseCharsHistoryEvidence && f.evidence.cursorQualEvidence && f.evidence.attrRectEvidence && f.evidence.eraseAttrsRegionEvidence);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => privateClipboardReceiptFixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v32' };
    assert.equal(includesRectChecksumEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesRectChecksumEvidence(empty, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v33' }), true, 'the current policy always carries the suffix');
    assert.equal(includesRectChecksumEvidence(empty, {}, earlier), false, 'an earlier policy without any artifact or declaration does not');
    assert.equal(includesRectChecksumEvidence(empty, {}, { ...earlier, rectChecksumSuiteId: 'x' }), true);
    assert.equal(includesRectChecksumEvidence(empty, { rectChecksumEvidence: {} }, earlier), true);
    assert.equal(includesRectChecksumEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
    rmSync(join(f.output, 'csum-observations.json')); rmSync(join(f.output, 'csum-run.json')); rmSync(join(f.output, 'csum-snapshots.jsonl')); rmSync(join(f.output, 'collector-csum.mjs'));
    assert.equal(includesRectChecksumEvidence(f.output, {}, earlier), false, 'the retained initial settings every run keeps never require it on their own');
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.rectChecksumEvidence; }],
    ['changed suite identity', f => { f.evidence.rectChecksumEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.rectChecksumEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.rectChecksumEvidence.counts = { inconclusive: 1 }; }],
    ['a stale observation digest', f => { f.evidence.rectChecksumEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.rectChecksumEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.rectChecksumEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.rectChecksumEvidence.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.rectChecksumContractsVersion = 2; }],
    ['a missing adapter declaration', f => { delete f.context.rectChecksumAdapterId; }],
    ['a missing suite declaration', f => { delete f.context.rectChecksumSuiteId; }],
    ['a changed adapter declaration', f => { f.context.rectChecksumAdapterId = 'other'; }],
    ['a changed adapter bundle declaration', f => { f.context.rectChecksumBundleSha256 = '0'.repeat(64); }],
    ['a missing column-editing predecessor', f => { delete f.evidence.colEditEvidence; }],
    ['a missing column-editing predecessor declaration', f => { delete f.context.colEditAdapterId; }],
    ['a changed column-editing predecessor bundle', f => { f.context.colEditBundleSha256 = '0'.repeat(64); }],
    ['the off-by-default report left off in the initial settings', f => { const settings = JSON.parse(readFileSync(join(f.output, 'initial-settings.json'))); delete settings.AllowRectangleChecksumReport; f.write('initial-settings.json', settings); f.context.initialSettingsSha256 = hash(readFileSync(join(f.output, 'initial-settings.json'))); }],
    ['changed initial settings bytes', f => { writeFileSync(join(f.output, 'initial-settings.json'), readFileSync(join(f.output, 'initial-settings.json'), 'utf8') + ' '); }],
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
]) test('the rectangle-checksum receipt refuses ' + label, () => privateClipboardReceiptFixture(f => { mutate(f); f.write('context.json', f.context); assert.throws(own(f), refusal); }));

test('the rectangle-checksum receipt refuses an adapter that is not the retained layered derivation', () => privateClipboardReceiptFixture(f => {
    writeFileSync(join(f.output, 'collector-csum.mjs'), readFileSync(join(f.output, 'collector-csum.mjs'), 'utf8') + '\n// edited\n');
    rebind(f, 'adapterBundleSha256', 'collector-csum.mjs');
    f.context.rectChecksumBundleSha256 = f.evidence.rectChecksumEvidence.adapterBundleSha256; f.write('context.json', f.context);
    assert.throws(own(f), /not the retained layered derivation/);
}));

test('the rectangle-checksum receipt refuses a changed run log even when the binding is rehashed to match', () => privateClipboardReceiptFixture(f => {
    const run = JSON.parse(readFileSync(join(f.output, 'csum-run.json')));
    run.cases[0].reads = run.cases[0].reads.slice(1);
    writeFileSync(join(f.output, 'csum-run.json'), JSON.stringify(run, null, 2) + '\n'); rebind(f, 'runSha256', 'csum-run.json');
    assert.throws(own(f), refusal);
}));

test('the rectangle-checksum receipt refuses a changed report that no longer equals its rederivation', () => privateClipboardReceiptFixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'csum-observations.json')));
    report.cases[0].conclusion.outcome = 'inconclusive'; report.cases[0].contractResult.outcome = 'inconclusive';
    writeFileSync(join(f.output, 'csum-observations.json'), JSON.stringify(report, null, 2) + '\n'); rebind(f, 'sha256', 'csum-observations.json');
    f.evidence.rectChecksumEvidence.counts = { inconclusive: 1 };
    assert.throws(f.finish, /observations do not match retained evidence/);
}));

test('the rectangle-checksum receipt refuses a truncated journal, an empty journal and a missing retained artifact', () => privateClipboardReceiptFixture(f => {
    const journal = join(f.output, 'csum-snapshots.jsonl'), lines = readFileSync(journal, 'utf8').trimEnd().split('\n');
    writeFileSync(journal, lines.slice(0, -1).join('\n') + '\n');
    assert.throws(own(f), refusal);
    writeFileSync(journal, '\n');
    assert.throws(own(f), /journal is empty/);
    writeFileSync(journal, lines.join('\n') + '\n');
    rmSync(join(f.output, 'csum-run.json'));
    assert.throws(own(f), refusal);
}));
