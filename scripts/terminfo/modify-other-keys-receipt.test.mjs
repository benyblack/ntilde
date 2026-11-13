import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
const privateClipboardReceiptFixture = action => baseFixture(action, { routed: true, modifyOtherKeys: true });
import { includesModifyOtherKeysEvidence, checkModifyOtherKeysEvidence } from './modify-other-keys-receipt.mjs';
import { modifyOtherKeysAdapterId } from './modify-other-keys-grading-collector.mjs';
import { modifyOtherKeysGradedSuiteId } from './modify-other-keys-contracts.mjs';
import { inputPlanV3, modifyOtherKeysFirstPolicy } from './input-plan.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Re-bind a changed retained file so only the semantic change is under test, not the stale digest. */
const rebind = (f, field, file) => { f.evidence.modifyOtherKeysEvidence[field] = hash(readFileSync(join(f.output, file))); };
/** This layer's own receipt check over the fixture's current files and declarations; refusals must be deliberate, never a TypeError. */
const own = f => () => checkModifyOtherKeysEvidence(f.output, f.evidence, f.context.runId, f.context, f.proof);
const refusal = error => !(error instanceof TypeError);
const predecessors = ['sgrColors', 'sgrRgb', 'sgrAttrs', 'sgrPalette', 'sgrTruecolor', 'sgrUnderline', 'sgrUnderlineColor', 'sgrReset', 'sgrClosure', 'eraseLine',
    'charEdit', 'eraseDisplay', 'lineEdit', 'charsets', 'scrollback', 'savedReset', 'eraseAttrsRegion', 'attrRect', 'cursorQual', 'eraseCharsHistory',
    'selectiveErase', 'rectEdit', 'colEdit', 'rectChecksum', 'lrMargins'];

test('a complete run carries the modifyOtherKeys suffix: counts, adapter identity, the v3 input suite, the routed-input layer and the layered derivation rederive without the collector', () => privateClipboardReceiptFixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.equal(f.finish().schemaVersion, 56);
    assert.deepEqual(f.evidence.modifyOtherKeysEvidence.counts, { 'inconclusive/supported': 1 });
    assert.equal(f.context.modifyOtherKeysAdapterId, modifyOtherKeysAdapterId);
    assert.equal(f.context.modifyOtherKeysSuiteId, modifyOtherKeysGradedSuiteId);
    assert.equal(f.evidence.inputEvidence.suiteId, inputPlanV3.suiteId);
    assert.deepEqual(f.evidence.inputEvidence.counts, { passed: 118 });
    assert.ok(f.evidence.routedInputEvidence !== undefined, 'the routed-input layer keeps deciding on the same journal');
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => privateClipboardReceiptFixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v46' };
    assert.equal(includesModifyOtherKeysEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesModifyOtherKeysEvidence(empty, {}, { sourceSnapshotPolicy: `readonly-owned-source-graph-v${modifyOtherKeysFirstPolicy}` }), true, 'the current policy always carries the suffix');
    assert.equal(includesModifyOtherKeysEvidence(empty, {}, earlier), false, 'an earlier policy without any artifact or declaration does not');
    assert.equal(includesModifyOtherKeysEvidence(empty, {}, { ...earlier, modifyOtherKeysSuiteId: 'x' }), true);
    assert.equal(includesModifyOtherKeysEvidence(empty, { modifyOtherKeysEvidence: {} }, earlier), true);
    assert.equal(includesModifyOtherKeysEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.modifyOtherKeysEvidence; }],
    ['changed suite identity', f => { f.evidence.modifyOtherKeysEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.modifyOtherKeysEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.modifyOtherKeysEvidence.counts = { 'inconclusive/inconclusive': 1 }; }],
    ['a stale observation digest', f => { f.evidence.modifyOtherKeysEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.modifyOtherKeysEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.modifyOtherKeysEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.modifyOtherKeysEvidence.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.modifyOtherKeysContractsVersion = 2; }],
    ['a missing adapter declaration', f => { delete f.context.modifyOtherKeysAdapterId; }],
    ['a missing suite declaration', f => { delete f.context.modifyOtherKeysSuiteId; }],
    ['a changed adapter declaration', f => { f.context.modifyOtherKeysAdapterId = 'other'; }],
    ['a changed adapter bundle declaration', f => { f.context.modifyOtherKeysBundleSha256 = '0'.repeat(64); }],
    ['an input suite that is not the v3 plan', f => { f.evidence.inputEvidence.suiteId = 'ntilde-owned-routed-input-v2'; }],
    ['a missing routed-input layer', f => { delete f.evidence.routedInputEvidence; }],
    ['a missing routed-input declaration', f => { delete f.context.routedInputAdapterId; }],
    ['an unavailable input suite', f => { f.evidence.inputEvidence.file = 'input-unavailable.json'; }],
    ...predecessors.flatMap(name => [
        [`a missing ${name} predecessor`, f => { delete f.evidence[`${name}Evidence`]; }],
        [`a missing ${name} predecessor declaration`, f => { delete f.context[`${name}AdapterId`]; }],
        [`a changed ${name} predecessor bundle`, f => { f.context[`${name}BundleSha256`] = '0'.repeat(64); }],
    ]),
]) test('the modifyOtherKeys receipt refuses ' + label, () => privateClipboardReceiptFixture(f => { mutate(f); f.write('context.json', f.context); assert.throws(own(f), refusal); }));

test('the modifyOtherKeys receipt refuses an adapter that is not the retained layered derivation', () => privateClipboardReceiptFixture(f => {
    writeFileSync(join(f.output, 'collector-mok.mjs'), readFileSync(join(f.output, 'collector-mok.mjs'), 'utf8') + '\n// edited\n');
    rebind(f, 'adapterBundleSha256', 'collector-mok.mjs');
    f.context.modifyOtherKeysBundleSha256 = f.evidence.modifyOtherKeysEvidence.adapterBundleSha256; f.write('context.json', f.context);
    assert.throws(own(f), /not the retained layered derivation/);
}));

test('the modifyOtherKeys receipt refuses a changed run log even when the binding is rehashed to match', () => privateClipboardReceiptFixture(f => {
    const run = JSON.parse(readFileSync(join(f.output, 'mok-run.json')));
    run.cases[0].served.declaredModifyOtherKeys = true;
    writeFileSync(join(f.output, 'mok-run.json'), JSON.stringify(run, null, 2) + '\n'); rebind(f, 'runSha256', 'mok-run.json');
    assert.throws(own(f), refusal);
}));

test('the modifyOtherKeys receipt refuses a changed observation report that no longer equals its rederivation', () => privateClipboardReceiptFixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'mok-observations.json')));
    report.cases[0].effectContract.outcome = 'inconclusive';
    writeFileSync(join(f.output, 'mok-observations.json'), JSON.stringify(report, null, 2) + '\n'); rebind(f, 'sha256', 'mok-observations.json');
    assert.throws(own(f), refusal);
}));

test('the modifyOtherKeys receipt refuses a run whose declared input suite is not the v3 plan', () => privateClipboardReceiptFixture(f => {
    f.context.inputSuiteId = 'ntilde-owned-routed-input-v2'; f.write('context.json', f.context);
    assert.throws(own(f), /requires the owned routed-input v3 suite plan/);
}));
