import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
const privateClipboardReceiptFixture = action => baseFixture(action, { routed: true, titleStack: true });
import { includesTitleStackEvidence, checkTitleStackEvidence, titleStackFirstPolicy } from './title-stack-receipt.mjs';
import { titleStackAdapterId } from './title-stack-grading-collector.mjs';
import { titleStackSuiteId } from './title-stack-contracts.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Re-bind a changed retained file so only the semantic change is under test, not the stale digest. */
const rebind = (f, field, file) => { f.evidence.titleStackEvidence[field] = hash(readFileSync(join(f.output, file))); };
/** This layer's own receipt check over the fixture's current files and declarations; refusals must be deliberate, never a TypeError. */
const own = f => () => checkTitleStackEvidence(f.output, f.evidence, f.context.runId, f.context);
const refusal = error => !(error instanceof TypeError);
const predecessors = ['sgrColors', 'sgrRgb', 'sgrAttrs', 'sgrPalette', 'sgrTruecolor', 'sgrUnderline', 'sgrUnderlineColor', 'sgrReset', 'sgrClosure', 'eraseLine',
    'charEdit', 'eraseDisplay', 'lineEdit', 'charsets', 'scrollback', 'savedReset', 'eraseAttrsRegion', 'attrRect', 'cursorQual', 'eraseCharsHistory',
    'selectiveErase', 'rectEdit', 'colEdit', 'rectChecksum', 'lrMargins'];

test('a complete run carries the Title stack suffix: counts, adapter identity and the layered derivation rederive without the collector', () => privateClipboardReceiptFixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.equal(f.finish().schemaVersion, 52);
    assert.deepEqual(f.evidence.titleStackEvidence.counts, { 'supported/supported': 2 });
    assert.equal(f.context.titleStackAdapterId, titleStackAdapterId);
    assert.equal(f.context.titleStackSuiteId, titleStackSuiteId);
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => privateClipboardReceiptFixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v45' };
    assert.equal(titleStackFirstPolicy, 46);
    assert.equal(includesTitleStackEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesTitleStackEvidence(empty, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v46' }), true, 'the current policy always carries the suffix');
    assert.equal(includesTitleStackEvidence(empty, {}, earlier), false, 'an earlier policy without any artifact or declaration does not');
    assert.equal(includesTitleStackEvidence(empty, {}, { ...earlier, titleStackSuiteId: 'x' }), true);
    assert.equal(includesTitleStackEvidence(empty, { titleStackEvidence: {} }, earlier), true);
    assert.equal(includesTitleStackEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.titleStackEvidence; }],
    ['changed suite identity', f => { f.evidence.titleStackEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.titleStackEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.titleStackEvidence.counts = { 'inconclusive/inconclusive': 2 }; }],
    ['a stale observation digest', f => { f.evidence.titleStackEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.titleStackEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.titleStackEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.titleStackEvidence.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.titleStackContractsVersion = 2; }],
    ['a missing adapter declaration', f => { delete f.context.titleStackAdapterId; }],
    ['a missing suite declaration', f => { delete f.context.titleStackSuiteId; }],
    ['a changed adapter declaration', f => { f.context.titleStackAdapterId = 'other'; }],
    ['a changed adapter bundle declaration', f => { f.context.titleStackBundleSha256 = '0'.repeat(64); }],
    ...predecessors.flatMap(name => [
        [`a missing ${name} predecessor`, f => { delete f.evidence[`${name}Evidence`]; }],
        [`a missing ${name} predecessor declaration`, f => { delete f.context[`${name}AdapterId`]; }],
        [`a changed ${name} predecessor bundle`, f => { f.context[`${name}BundleSha256`] = '0'.repeat(64); }],
    ]),
]) test('the Title stack receipt refuses ' + label, () => privateClipboardReceiptFixture(f => { mutate(f); f.write('context.json', f.context); assert.throws(own(f), refusal); }));

test('the Title stack receipt refuses an adapter that is not the retained layered derivation', () => privateClipboardReceiptFixture(f => {
    writeFileSync(join(f.output, 'collector-tst.mjs'), readFileSync(join(f.output, 'collector-tst.mjs'), 'utf8') + '\n// edited\n');
    rebind(f, 'adapterBundleSha256', 'collector-tst.mjs');
    f.context.titleStackBundleSha256 = f.evidence.titleStackEvidence.adapterBundleSha256; f.write('context.json', f.context);
    assert.throws(own(f), /not the retained layered derivation/);
}));

test('the Title stack receipt refuses a changed run log even when the binding is rehashed to match', () => privateClipboardReceiptFixture(f => {
    const run = JSON.parse(readFileSync(join(f.output, 'tst-run.json')));
    run.pinned[1].termless.result.pass = false;
    writeFileSync(join(f.output, 'tst-run.json'), JSON.stringify(run, null, 2) + '\n'); rebind(f, 'runSha256', 'tst-run.json');
    assert.throws(own(f), refusal);
}));

test('the Title stack receipt refuses a changed report that no longer equals its rederivation', () => privateClipboardReceiptFixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'tst-observations.json')));
    report.cases[0].effectContract.outcome = 'unsupported';
    writeFileSync(join(f.output, 'tst-observations.json'), JSON.stringify(report, null, 2) + '\n'); rebind(f, 'sha256', 'tst-observations.json');
    f.evidence.titleStackEvidence.counts = { 'supported/supported': 1, 'supported/unsupported': 1 };
    assert.throws(f.finish, /observations do not match retained evidence/);
}));

test('the Title stack receipt refuses journal frames edited in the retained journal, a truncated journal and a missing retained artifact', () => privateClipboardReceiptFixture(f => {
    const journal = join(f.output, 'tst-snapshots.jsonl'), lines = readFileSync(journal, 'utf8').trimEnd().split('\n');
    const edited = JSON.parse(lines[7]); edited.before.metadata.oscTitle = 'edited-in-the-journal';
    writeFileSync(journal, [...lines.slice(0, 7), JSON.stringify(edited), ...lines.slice(8)].join('\n') + '\n');
    assert.throws(own(f), refusal, 'an edited frame');
    writeFileSync(journal, lines.slice(0, -1).join('\n') + '\n');
    assert.throws(own(f), refusal, 'a truncated journal');
    writeFileSync(journal, '');
    assert.throws(own(f), /journal is empty/, 'an emptied journal');
    rmSync(journal);
    assert.throws(own(f), refusal, 'a missing journal');
}));

test('the Title stack receipt refuses a retained file that is not a regular file', () => privateClipboardReceiptFixture(f => {
    rmSync(join(f.output, 'tst-run.json'));
    writeFileSync(join(f.output, 'tst-run-target.json'), '{}');
    assert.throws(own(f), refusal);
}));

test('a typed unavailable marker keeps no graded artifact, and cannot be forged beside one', () => privateClipboardReceiptFixture(f => {
    const marker = { schemaVersion: 1, runId: f.context.runId, suiteId: titleStackSuiteId, status: 'unavailable', reason: 'paneMetadataUnavailable', plannedCases: 2 };
    const bytes = JSON.stringify(marker) + '\n';
    for (const file of ['tst-observations.json', 'tst-run.json']) rmSync(join(f.output, file));
    writeFileSync(join(f.output, 'tst-snapshots.jsonl'), '');
    writeFileSync(join(f.output, 'tst-unavailable.json'), bytes);
    const binding = { runId: f.context.runId, suiteId: titleStackSuiteId, file: 'tst-unavailable.json', sha256: hash(bytes), status: 'unavailable', reason: 'paneMetadataUnavailable', counts: {},
        adapterBundleSha256: f.evidence.titleStackEvidence.adapterBundleSha256, contractsVersion: f.evidence.titleStackEvidence.contractsVersion, provisionalUntil: 'cell-receipt.json' };
    f.evidence.titleStackEvidence = binding;
    assert.doesNotThrow(own(f), 'a typed unavailable record is a complete outcome');
    f.evidence.titleStackEvidence = { ...binding, reason: 'invented' };
    assert.throws(own(f), /Invalid Title stack unavailable receipt binding/);
    f.evidence.titleStackEvidence = { ...binding, counts: { 'supported/supported': 2 } };
    assert.throws(own(f), /Invalid Title stack unavailable receipt binding/);
    f.evidence.titleStackEvidence = binding;
    writeFileSync(join(f.output, 'tst-run.json'), '{}');
    assert.throws(own(f), /retains graded artifacts/);
}));
