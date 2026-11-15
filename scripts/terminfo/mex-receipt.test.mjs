import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
const privateClipboardReceiptFixture = action => baseFixture(action, { routed: true, mex: true });
import { includesMexEvidence, checkMexEvidence } from './mex-receipt.mjs';
import { mexAdapterId } from './mex-grading-collector.mjs';
import { mexGradedSuiteId } from './mex-contracts.mjs';
import { inputPlanV4, mexFirstPolicy } from './input-plan.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Re-bind a changed retained file so only the semantic change is under test, not the stale digest. */
const rebind = (f, field, file) => { f.evidence.mexEvidence[field] = hash(readFileSync(join(f.output, file))); };
/** This layer's own receipt check over the fixture's current files and declarations; refusals must be deliberate, never a TypeError. */
const own = f => () => checkMexEvidence(f.output, f.evidence, f.context.runId, f.context, f.proof);
const refusal = error => !(error instanceof TypeError);
const predecessors = ['sgrColors', 'sgrRgb', 'sgrAttrs', 'sgrPalette', 'sgrTruecolor', 'sgrUnderline', 'sgrUnderlineColor', 'sgrReset', 'sgrClosure', 'eraseLine',
    'charEdit', 'eraseDisplay', 'lineEdit', 'charsets', 'scrollback', 'savedReset', 'eraseAttrsRegion', 'attrRect', 'cursorQual', 'eraseCharsHistory',
    'selectiveErase', 'rectEdit', 'colEdit', 'rectChecksum', 'lrMargins'];

test('a complete run carries the extended-mouse suffix: counts, adapter identity, the v4 input suite, the routed-input layer and the layered derivation rederive without the collector', () => privateClipboardReceiptFixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.equal(f.finish().schemaVersion, 63);
    assert.deepEqual(f.evidence.mexEvidence.counts, { 'inconclusive/supported': 2 });
    assert.equal(f.context.mexAdapterId, mexAdapterId);
    assert.equal(f.context.mexSuiteId, mexGradedSuiteId);
    assert.equal(f.evidence.inputEvidence.suiteId, inputPlanV4.suiteId);
    assert.deepEqual(f.evidence.inputEvidence.counts, { passed: 164 });
    assert.ok(f.evidence.routedInputEvidence !== undefined, 'the routed-input layer keeps deciding on the same journal');
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => privateClipboardReceiptFixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v46' };
    assert.equal(includesMexEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesMexEvidence(empty, {}, { sourceSnapshotPolicy: `readonly-owned-source-graph-v${mexFirstPolicy}` }), true, 'the current policy always carries the suffix');
    assert.equal(includesMexEvidence(empty, {}, earlier), false, 'an earlier policy without any artifact or declaration does not');
    assert.equal(includesMexEvidence(empty, {}, { ...earlier, mexSuiteId: 'x' }), true);
    assert.equal(includesMexEvidence(empty, { mexEvidence: {} }, earlier), true);
    assert.equal(includesMexEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.mexEvidence; }],
    ['changed suite identity', f => { f.evidence.mexEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.mexEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.mexEvidence.counts = { 'inconclusive/inconclusive': 1 }; }],
    ['a stale observation digest', f => { f.evidence.mexEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.mexEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.mexEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.mexEvidence.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.mexContractsVersion = 2; }],
    ['a missing adapter declaration', f => { delete f.context.mexAdapterId; }],
    ['a missing suite declaration', f => { delete f.context.mexSuiteId; }],
    ['a changed adapter declaration', f => { f.context.mexAdapterId = 'other'; }],
    ['a changed adapter bundle declaration', f => { f.context.mexBundleSha256 = '0'.repeat(64); }],
    ['an input suite that is not the v4 plan', f => { f.evidence.inputEvidence.suiteId = 'ntilde-owned-routed-input-v3'; }],
    ['a missing routed-input layer', f => { delete f.evidence.routedInputEvidence; }],
    ['a missing routed-input declaration', f => { delete f.context.routedInputAdapterId; }],
    ['an unavailable input suite', f => { f.evidence.inputEvidence.file = 'input-unavailable.json'; }],
    ...predecessors.flatMap(name => [
        [`a missing ${name} predecessor`, f => { delete f.evidence[`${name}Evidence`]; }],
        [`a missing ${name} predecessor declaration`, f => { delete f.context[`${name}AdapterId`]; }],
        [`a changed ${name} predecessor bundle`, f => { f.context[`${name}BundleSha256`] = '0'.repeat(64); }],
    ]),
]) test('the extended-mouse receipt refuses ' + label, () => privateClipboardReceiptFixture(f => { mutate(f); f.write('context.json', f.context); assert.throws(own(f), refusal); }));

test('the extended-mouse receipt refuses an adapter that is not the retained layered derivation', () => privateClipboardReceiptFixture(f => {
    writeFileSync(join(f.output, 'collector-mex.mjs'), readFileSync(join(f.output, 'collector-mex.mjs'), 'utf8') + '\n// edited\n');
    rebind(f, 'adapterBundleSha256', 'collector-mex.mjs');
    f.context.mexBundleSha256 = f.evidence.mexEvidence.adapterBundleSha256; f.write('context.json', f.context);
    assert.throws(own(f), /not the retained layered derivation/);
}));

test('the extended-mouse receipt refuses a changed run log even when the binding is rehashed to match', () => privateClipboardReceiptFixture(f => {
    const run = JSON.parse(readFileSync(join(f.output, 'mex-run.json')));
    run.cases[0].served.on = false;
    writeFileSync(join(f.output, 'mex-run.json'), JSON.stringify(run, null, 2) + '\n'); rebind(f, 'runSha256', 'mex-run.json');
    assert.throws(own(f), refusal);
}));

test('the extended-mouse receipt refuses a changed observation report that no longer equals its rederivation', () => privateClipboardReceiptFixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'mex-observations.json')));
    report.cases[0].effectContract.outcome = 'inconclusive';
    writeFileSync(join(f.output, 'mex-observations.json'), JSON.stringify(report, null, 2) + '\n'); rebind(f, 'sha256', 'mex-observations.json');
    assert.throws(own(f), refusal);
}));

test('the extended-mouse receipt refuses a run whose declared input suite is not the v4 plan', () => privateClipboardReceiptFixture(f => {
    f.context.inputSuiteId = 'ntilde-owned-routed-input-v3'; f.write('context.json', f.context);
    assert.throws(own(f), /requires the owned routed-input v4 suite plan/);
}));

test('the earlier input layers keep re-deriving from the v4 journal: routed-input and modifyOtherKeys accept it, a v3 declaration for it does not', () => privateClipboardReceiptFixture(async f => {
    const { checkRoutedInputEvidence } = await import('./routed-input-receipt.mjs');
    const { checkModifyOtherKeysEvidence } = await import('./modify-other-keys-receipt.mjs');
    assert.doesNotThrow(() => checkRoutedInputEvidence(f.output, f.evidence, f.context.runId, f.context, f.proof));
    assert.doesNotThrow(() => checkModifyOtherKeysEvidence(f.output, f.evidence, f.context.runId, f.context, f.proof));
    assert.deepEqual(f.evidence.routedInputEvidence.counts, { 'inconclusive/supported': 3 });
    assert.deepEqual(f.evidence.modifyOtherKeysEvidence.counts, { 'inconclusive/supported': 1 });
}));

test('the extended-mouse receipt refuses a changed routed event in the shared journal and a changed 1005 control capture', () => privateClipboardReceiptFixture(f => {
    const lines = readFileSync(join(f.output, 'input-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const control = lines.find(frame => frame.kind === 'input-control' && frame.caseId === 'input.mex.u.pinned.set');
    control.capture.hex = control.capture.hex.replace(Buffer.from('\x1b[?1005;1$y').toString('hex'), Buffer.from('\x1b[?1005;2$y').toString('hex'));
    writeFileSync(join(f.output, 'input-snapshots.jsonl'), lines.map(frame => JSON.stringify(frame)).join('\n') + '\n');
    assert.throws(own(f), refusal);
}));
