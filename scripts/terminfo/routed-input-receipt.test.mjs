import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture } from './fixtures/private-clipboard-receipt.mjs';
import { includesRoutedInputEvidence, checkRoutedInputEvidence } from './routed-input-receipt.mjs';
import { routedInputAdapterId } from './routed-input-grading-collector.mjs';
import { routedInputGradedSuiteId } from './routed-input-contracts.mjs';
import { inputPlanV2 } from './input-plan.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Re-bind a changed retained file so only the semantic change is under test, not the stale digest. */
const rebind = (f, field, file) => { f.evidence.routedInputEvidence[field] = hash(readFileSync(join(f.output, file))); };
/** This layer's own receipt check over the fixture's current files and declarations; refusals must be deliberate, never a TypeError. */
const own = f => () => checkRoutedInputEvidence(f.output, f.evidence, f.context.runId, f.context, f.proof);
const refusal = error => !(error instanceof TypeError);
const predecessors = ['sgrColors', 'sgrRgb', 'sgrAttrs', 'sgrPalette', 'sgrTruecolor', 'sgrUnderline', 'sgrUnderlineColor', 'sgrReset', 'sgrClosure', 'eraseLine',
    'charEdit', 'eraseDisplay', 'lineEdit', 'charsets', 'scrollback', 'savedReset', 'eraseAttrsRegion', 'attrRect', 'cursorQual', 'eraseCharsHistory',
    'selectiveErase', 'rectEdit', 'colEdit', 'rectChecksum', 'lrMargins'];

test('a complete run carries the routed-input suffix: counts, adapter identity, owned input suite and the layered derivation rederive without the collector', () => privateClipboardReceiptFixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.equal(f.finish().schemaVersion, 41);
    assert.deepEqual(f.evidence.routedInputEvidence.counts, { 'inconclusive/supported': 3 });
    assert.equal(f.context.routedInputAdapterId, routedInputAdapterId);
    assert.equal(f.context.routedInputSuiteId, routedInputGradedSuiteId);
    assert.equal(f.evidence.inputEvidence.suiteId, inputPlanV2.suiteId);
    assert.deepEqual(f.evidence.inputEvidence.counts, { passed: 60 });
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => privateClipboardReceiptFixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v34' };
    assert.equal(includesRoutedInputEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesRoutedInputEvidence(empty, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v35' }), true, 'the current policy always carries the suffix');
    assert.equal(includesRoutedInputEvidence(empty, {}, earlier), false, 'an earlier policy without any artifact or declaration does not');
    assert.equal(includesRoutedInputEvidence(empty, {}, { ...earlier, routedInputSuiteId: 'x' }), true);
    assert.equal(includesRoutedInputEvidence(empty, { routedInputEvidence: {} }, earlier), true);
    assert.equal(includesRoutedInputEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.routedInputEvidence; }],
    ['changed suite identity', f => { f.evidence.routedInputEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.routedInputEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.routedInputEvidence.counts = { 'inconclusive/inconclusive': 3 }; }],
    ['a stale observation digest', f => { f.evidence.routedInputEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.routedInputEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.routedInputEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.routedInputEvidence.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.routedInputContractsVersion = 2; }],
    ['a missing adapter declaration', f => { delete f.context.routedInputAdapterId; }],
    ['a missing suite declaration', f => { delete f.context.routedInputSuiteId; }],
    ['a changed adapter declaration', f => { f.context.routedInputAdapterId = 'other'; }],
    ['a changed adapter bundle declaration', f => { f.context.routedInputBundleSha256 = '0'.repeat(64); }],
    ['an input suite that is not the benchmark plan', f => { f.evidence.inputEvidence.suiteId = 'ntilde-owned-routed-input-v1'; }],
    ['an unavailable input suite', f => { f.evidence.inputEvidence.file = 'input-unavailable.json'; }],
    ...predecessors.flatMap(name => [
        [`a missing ${name} predecessor`, f => { delete f.evidence[`${name}Evidence`]; }],
        [`a missing ${name} predecessor declaration`, f => { delete f.context[`${name}AdapterId`]; }],
        [`a changed ${name} predecessor bundle`, f => { f.context[`${name}BundleSha256`] = '0'.repeat(64); }],
    ]),
]) test('the routed-input receipt refuses ' + label, () => privateClipboardReceiptFixture(f => { mutate(f); f.write('context.json', f.context); assert.throws(own(f), refusal); }));

test('the routed-input receipt refuses an adapter that is not the retained layered derivation', () => privateClipboardReceiptFixture(f => {
    writeFileSync(join(f.output, 'collector-rin.mjs'), readFileSync(join(f.output, 'collector-rin.mjs'), 'utf8') + '\n// edited\n');
    rebind(f, 'adapterBundleSha256', 'collector-rin.mjs');
    f.context.routedInputBundleSha256 = f.evidence.routedInputEvidence.adapterBundleSha256; f.write('context.json', f.context);
    assert.throws(own(f), /not the retained layered derivation/);
}));

test('the routed-input receipt refuses a changed run log even when the binding is rehashed to match', () => privateClipboardReceiptFixture(f => {
    const run = JSON.parse(readFileSync(join(f.output, 'rin-run.json')));
    run.cases[2].served.on = false;
    writeFileSync(join(f.output, 'rin-run.json'), JSON.stringify(run, null, 2) + '\n'); rebind(f, 'runSha256', 'rin-run.json');
    assert.throws(own(f), refusal);
}));

test('the routed-input receipt refuses a changed report that no longer equals its rederivation', () => privateClipboardReceiptFixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'rin-observations.json')));
    report.cases[0].effectContract.outcome = 'unsupported';
    writeFileSync(join(f.output, 'rin-observations.json'), JSON.stringify(report, null, 2) + '\n'); rebind(f, 'sha256', 'rin-observations.json');
    f.evidence.routedInputEvidence.counts = { 'inconclusive/supported': 2, 'inconclusive/unsupported': 1 };
    assert.throws(f.finish, /observations do not match retained evidence/);
}));

test('the routed-input receipt refuses child bytes edited in the owned input journal, a truncated journal and a missing retained artifact', () => privateClipboardReceiptFixture(f => {
    const journal = join(f.output, 'input-snapshots.jsonl'), lines = readFileSync(journal, 'utf8').trimEnd().split('\n');
    const frames = lines.map(line => JSON.parse(line)), read = frames.findIndex(frame => frame.kind === 'input-read' && frame.caseId === 'input.mouse.x10.left.ctrl.raw');
    frames[read].capture.hex = '1b5b4d308023';
    writeFileSync(journal, frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
    assert.throws(own(f), refusal);
    writeFileSync(journal, lines.slice(0, -1).join('\n') + '\n');
    assert.throws(own(f), refusal);
    writeFileSync(journal, lines.join('\n') + '\n');
    rmSync(join(f.output, 'rin-run.json'));
    assert.throws(own(f), refusal);
}));
