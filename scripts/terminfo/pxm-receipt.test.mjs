import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
const privateClipboardReceiptFixture = action => baseFixture(action, { routed: true, mex: true, pxm: true });
import { includesPxmEvidence, checkPxmEvidence } from './pxm-receipt.mjs';
import { pxmAdapterId } from './pxm-grading-collector.mjs';
import { pxmGradedSuiteId } from './pxm-contracts.mjs';
import { inputPlanV5, pxmFirstPolicy } from './input-plan.mjs';

// The newest receipt schema: the suffix order puts the pixel layer last (filled by the integration step).
const receiptSchema = 67;
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Re-bind a changed retained file so only the semantic change is under test, not the stale digest. */
const rebind = (f, field, file) => { f.evidence.pxmEvidence[field] = hash(readFileSync(join(f.output, file))); };
/** This layer's own receipt check over the fixture's current files and declarations; refusals must be deliberate, never a TypeError. */
const own = f => () => checkPxmEvidence(f.output, f.evidence, f.context.runId, f.context, f.proof);
const refusal = error => !(error instanceof TypeError);
const predecessors = ['sgrColors', 'sgrRgb', 'sgrAttrs', 'sgrPalette', 'sgrTruecolor', 'sgrUnderline', 'sgrUnderlineColor', 'sgrReset', 'sgrClosure', 'eraseLine',
    'charEdit', 'eraseDisplay', 'lineEdit', 'charsets', 'scrollback', 'savedReset', 'eraseAttrsRegion', 'attrRect', 'cursorQual', 'eraseCharsHistory',
    'selectiveErase', 'rectEdit', 'colEdit', 'rectChecksum', 'lrMargins'];

test('a complete run carries the SGR-pixel suffix: counts, adapter identity, the v5 input suite, the routed-input layer and the layered derivation rederive without the collector', () => privateClipboardReceiptFixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.equal(f.finish().schemaVersion, receiptSchema);
    assert.deepEqual(f.evidence.pxmEvidence.counts, { 'inconclusive/supported': 1 });
    assert.equal(f.context.pxmAdapterId, pxmAdapterId);
    assert.equal(f.context.pxmSuiteId, pxmGradedSuiteId);
    assert.equal(f.evidence.inputEvidence.suiteId, inputPlanV5.suiteId);
    assert.deepEqual(f.evidence.inputEvidence.counts, { passed: 196 });
    assert.ok(f.evidence.routedInputEvidence !== undefined, 'the routed-input layer keeps deciding on the same journal');
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => privateClipboardReceiptFixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v46' };
    assert.equal(includesPxmEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesPxmEvidence(empty, {}, { sourceSnapshotPolicy: `readonly-owned-source-graph-v${pxmFirstPolicy}` }), true, 'the current policy always carries the suffix');
    assert.equal(includesPxmEvidence(empty, {}, earlier), false, 'an earlier policy without any artifact or declaration does not');
    assert.equal(includesPxmEvidence(empty, {}, { ...earlier, pxmSuiteId: 'x' }), true);
    assert.equal(includesPxmEvidence(empty, { pxmEvidence: {} }, earlier), true);
    assert.equal(includesPxmEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.pxmEvidence; }],
    ['changed suite identity', f => { f.evidence.pxmEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.pxmEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.pxmEvidence.counts = { 'inconclusive/inconclusive': 1 }; }],
    ['a stale observation digest', f => { f.evidence.pxmEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.pxmEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.pxmEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.pxmEvidence.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.pxmContractsVersion = 2; }],
    ['a missing adapter declaration', f => { delete f.context.pxmAdapterId; }],
    ['a missing suite declaration', f => { delete f.context.pxmSuiteId; }],
    ['a changed adapter declaration', f => { f.context.pxmAdapterId = 'other'; }],
    ['a changed adapter bundle declaration', f => { f.context.pxmBundleSha256 = '0'.repeat(64); }],
    ['an input suite that is not the v5 plan', f => { f.evidence.inputEvidence.suiteId = 'ntilde-owned-routed-input-v4'; }],
    ['a missing routed-input layer', f => { delete f.evidence.routedInputEvidence; }],
    ['a missing extended-mouse layer', f => { delete f.evidence.mexEvidence; }],
    ['a missing extended-mouse declaration', f => { delete f.context.mexAdapterId; }],
    ['a missing routed-input declaration', f => { delete f.context.routedInputAdapterId; }],
    ['an unavailable input suite', f => { f.evidence.inputEvidence.file = 'input-unavailable.json'; }],
    ...predecessors.flatMap(name => [
        [`a missing ${name} predecessor`, f => { delete f.evidence[`${name}Evidence`]; }],
        [`a missing ${name} predecessor declaration`, f => { delete f.context[`${name}AdapterId`]; }],
        [`a changed ${name} predecessor bundle`, f => { f.context[`${name}BundleSha256`] = '0'.repeat(64); }],
    ]),
]) test('the SGR-pixel receipt refuses ' + label, () => privateClipboardReceiptFixture(f => { mutate(f); f.write('context.json', f.context); assert.throws(own(f), refusal); }));

test('the SGR-pixel receipt refuses an adapter that is not the retained layered derivation', () => privateClipboardReceiptFixture(f => {
    writeFileSync(join(f.output, 'collector-pxm.mjs'), readFileSync(join(f.output, 'collector-pxm.mjs'), 'utf8') + '\n// edited\n');
    rebind(f, 'adapterBundleSha256', 'collector-pxm.mjs');
    f.context.pxmBundleSha256 = f.evidence.pxmEvidence.adapterBundleSha256; f.write('context.json', f.context);
    assert.throws(own(f), /not the retained layered derivation/);
}));

test('the SGR-pixel receipt refuses a changed run log even when the binding is rehashed to match', () => privateClipboardReceiptFixture(f => {
    const run = JSON.parse(readFileSync(join(f.output, 'pxm-run.json')));
    run.cases[0].served.on = false;
    writeFileSync(join(f.output, 'pxm-run.json'), JSON.stringify(run, null, 2) + '\n'); rebind(f, 'runSha256', 'pxm-run.json');
    assert.throws(own(f), refusal);
}));

test('the SGR-pixel receipt refuses a changed observation report that no longer equals its rederivation', () => privateClipboardReceiptFixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'pxm-observations.json')));
    report.cases[0].effectContract.outcome = 'inconclusive';
    writeFileSync(join(f.output, 'pxm-observations.json'), JSON.stringify(report, null, 2) + '\n'); rebind(f, 'sha256', 'pxm-observations.json');
    assert.throws(own(f), refusal);
}));

test('the SGR-pixel receipt refuses an input suite that predates the pixel-coordinate plan', () => privateClipboardReceiptFixture(f => {
    f.context.inputSuiteId = 'ntilde-owned-routed-input-v4'; f.write('context.json', f.context);
    assert.throws(own(f), /requires the owned routed-input v5 or later suite plan/);
}));

test('the earlier input layers keep re-deriving from the v5 journal: routed-input, modifyOtherKeys and extended-mouse accept it', () => privateClipboardReceiptFixture(async f => {
    const { checkRoutedInputEvidence } = await import('./routed-input-receipt.mjs');
    const { checkModifyOtherKeysEvidence } = await import('./modify-other-keys-receipt.mjs');
    const { checkMexEvidence } = await import('./mex-receipt.mjs');
    assert.doesNotThrow(() => checkRoutedInputEvidence(f.output, f.evidence, f.context.runId, f.context, f.proof));
    assert.doesNotThrow(() => checkModifyOtherKeysEvidence(f.output, f.evidence, f.context.runId, f.context, f.proof));
    assert.doesNotThrow(() => checkMexEvidence(f.output, f.evidence, f.context.runId, f.context, f.proof));
    assert.deepEqual(f.evidence.mexEvidence.counts, { 'inconclusive/supported': 2 });
    assert.deepEqual(f.evidence.routedInputEvidence.counts, { 'inconclusive/supported': 3 });
    assert.deepEqual(f.evidence.modifyOtherKeysEvidence.counts, { 'inconclusive/supported': 1 });
}));

test('the SGR-pixel receipt refuses a changed routed event in the shared journal and a changed 1016 control capture', () => privateClipboardReceiptFixture(f => {
    const lines = readFileSync(join(f.output, 'input-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const control = lines.find(frame => frame.kind === 'input-control' && frame.caseId === 'input.pxm.alone.set');
    control.capture.hex = control.capture.hex.replace(Buffer.from('\x1b[?1016;1$y').toString('hex'), Buffer.from('\x1b[?1016;2$y').toString('hex'));
    writeFileSync(join(f.output, 'input-snapshots.jsonl'), lines.map(frame => JSON.stringify(frame)).join('\n') + '\n');
    assert.throws(own(f), refusal);
}));
