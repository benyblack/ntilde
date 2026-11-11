import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
const privateClipboardReceiptFixture = action => baseFixture(action, { routed: true });
import { includesOsc78Evidence, checkOsc78Evidence, osc78FirstPolicy } from './osc78-receipt.mjs';
import { osc78AdapterId } from './osc78-grading-collector.mjs';
import { osc78SuiteId } from './osc78-contracts.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Re-bind a changed retained file so only the semantic change is under test, not the stale digest. */
const rebind = (f, field, file) => { f.evidence.osc78Evidence[field] = hash(readFileSync(join(f.output, file))); };
/** This layer's own receipt check over the fixture's current files and declarations; refusals must be deliberate, never a TypeError. */
const own = f => () => checkOsc78Evidence(f.output, f.evidence, f.context.runId, f.context);
const refusal = error => !(error instanceof TypeError);
const predecessors = ['sgrColors', 'sgrRgb', 'sgrAttrs', 'sgrPalette', 'sgrTruecolor', 'sgrUnderline', 'sgrUnderlineColor', 'sgrReset', 'sgrClosure', 'eraseLine',
    'charEdit', 'eraseDisplay', 'lineEdit', 'charsets', 'scrollback', 'savedReset', 'eraseAttrsRegion', 'attrRect', 'cursorQual', 'eraseCharsHistory',
    'selectiveErase', 'rectEdit', 'colEdit', 'rectChecksum', 'lrMargins'];

test('a complete run carries the OSC 7/8 suffix: counts, adapter identity and the layered derivation rederive without the collector', () => privateClipboardReceiptFixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.equal(f.finish().schemaVersion, 51);
    assert.deepEqual(f.evidence.osc78Evidence.counts, { 'inconclusive/supported': 1, 'supported/supported': 1 });
    assert.equal(f.context.osc78AdapterId, osc78AdapterId);
    assert.equal(f.context.osc78SuiteId, osc78SuiteId);
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => privateClipboardReceiptFixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v44' };
    assert.equal(osc78FirstPolicy, 45);
    assert.equal(includesOsc78Evidence(f.output, f.evidence, f.context), true);
    assert.equal(includesOsc78Evidence(empty, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v45' }), true, 'the current policy always carries the suffix');
    assert.equal(includesOsc78Evidence(empty, {}, earlier), false, 'an earlier policy without any artifact or declaration does not');
    assert.equal(includesOsc78Evidence(empty, {}, { ...earlier, osc78SuiteId: 'x' }), true);
    assert.equal(includesOsc78Evidence(empty, { osc78Evidence: {} }, earlier), true);
    assert.equal(includesOsc78Evidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.osc78Evidence; }],
    ['changed suite identity', f => { f.evidence.osc78Evidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.osc78Evidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.osc78Evidence.counts = { 'inconclusive/inconclusive': 2 }; }],
    ['a stale observation digest', f => { f.evidence.osc78Evidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.osc78Evidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.osc78Evidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.osc78Evidence.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.osc78ContractsVersion = 2; }],
    ['a missing adapter declaration', f => { delete f.context.osc78AdapterId; }],
    ['a missing suite declaration', f => { delete f.context.osc78SuiteId; }],
    ['a changed adapter declaration', f => { f.context.osc78AdapterId = 'other'; }],
    ['a changed adapter bundle declaration', f => { f.context.osc78BundleSha256 = '0'.repeat(64); }],
    ...predecessors.flatMap(name => [
        [`a missing ${name} predecessor`, f => { delete f.evidence[`${name}Evidence`]; }],
        [`a missing ${name} predecessor declaration`, f => { delete f.context[`${name}AdapterId`]; }],
        [`a changed ${name} predecessor bundle`, f => { f.context[`${name}BundleSha256`] = '0'.repeat(64); }],
    ]),
]) test('the OSC 7/8 receipt refuses ' + label, () => privateClipboardReceiptFixture(f => { mutate(f); f.write('context.json', f.context); assert.throws(own(f), refusal); }));

test('the OSC 7/8 receipt refuses an adapter that is not the retained layered derivation', () => privateClipboardReceiptFixture(f => {
    writeFileSync(join(f.output, 'collector-lnk.mjs'), readFileSync(join(f.output, 'collector-lnk.mjs'), 'utf8') + '\n// edited\n');
    rebind(f, 'adapterBundleSha256', 'collector-lnk.mjs');
    f.context.osc78BundleSha256 = f.evidence.osc78Evidence.adapterBundleSha256; f.write('context.json', f.context);
    assert.throws(own(f), /not the retained layered derivation/);
}));

test('the OSC 7/8 receipt refuses a changed run log even when the binding is rehashed to match', () => privateClipboardReceiptFixture(f => {
    const run = JSON.parse(readFileSync(join(f.output, 'lnk-run.json')));
    run.pinned[1].termless.result.pass = false;
    writeFileSync(join(f.output, 'lnk-run.json'), JSON.stringify(run, null, 2) + '\n'); rebind(f, 'runSha256', 'lnk-run.json');
    assert.throws(own(f), refusal);
}));

test('the OSC 7/8 receipt refuses a changed report that no longer equals its rederivation', () => privateClipboardReceiptFixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'lnk-observations.json')));
    report.cases[0].effectContract.outcome = 'unsupported';
    writeFileSync(join(f.output, 'lnk-observations.json'), JSON.stringify(report, null, 2) + '\n'); rebind(f, 'sha256', 'lnk-observations.json');
    f.evidence.osc78Evidence.counts = { 'inconclusive/unsupported': 1, 'supported/supported': 1 };
    assert.throws(f.finish, /observations do not match retained evidence/);
}));

test('the OSC 7/8 receipt refuses journal frames edited in the retained journal, a truncated journal and a missing retained artifact', () => privateClipboardReceiptFixture(f => {
    const journal = join(f.output, 'lnk-snapshots.jsonl'), lines = readFileSync(journal, 'utf8').trimEnd().split('\n');
    const edited = JSON.parse(lines[7]); edited.before.metadata.buffer.links = edited.before.metadata.buffer.links.length ? [] : [{ row: 0, col: 1, length: 4, identity: 0, uri: 'https://example.invalid/forged', id: null }];
    writeFileSync(journal, [...lines.slice(0, 7), JSON.stringify(edited), ...lines.slice(8)].join('\n') + '\n');
    assert.throws(own(f), refusal, 'an edited frame');
    writeFileSync(journal, lines.slice(0, -1).join('\n') + '\n');
    assert.throws(own(f), refusal, 'a truncated journal');
    writeFileSync(journal, '');
    assert.throws(own(f), /journal is empty/, 'an emptied journal');
    rmSync(journal);
    assert.throws(own(f), refusal, 'a missing journal');
}));

test('the OSC 7/8 receipt refuses a retained file that is not a regular file', () => privateClipboardReceiptFixture(f => {
    rmSync(join(f.output, 'lnk-run.json'));
    writeFileSync(join(f.output, 'lnk-run-target.json'), '{}');
    assert.throws(own(f), refusal);
}));

test('a typed unavailable marker keeps no graded artifact, and cannot be forged beside one', () => privateClipboardReceiptFixture(f => {
    const marker = { schemaVersion: 1, runId: f.context.runId, suiteId: osc78SuiteId, status: 'unavailable', reason: 'paneMetadataUnavailable', plannedCases: 2 };
    const bytes = JSON.stringify(marker) + '\n';
    for (const file of ['lnk-observations.json', 'lnk-run.json']) rmSync(join(f.output, file));
    writeFileSync(join(f.output, 'lnk-snapshots.jsonl'), '');
    writeFileSync(join(f.output, 'lnk-unavailable.json'), bytes);
    const binding = { runId: f.context.runId, suiteId: osc78SuiteId, file: 'lnk-unavailable.json', sha256: hash(bytes), status: 'unavailable', reason: 'paneMetadataUnavailable', counts: {},
        adapterBundleSha256: f.evidence.osc78Evidence.adapterBundleSha256, contractsVersion: f.evidence.osc78Evidence.contractsVersion, provisionalUntil: 'cell-receipt.json' };
    f.evidence.osc78Evidence = binding;
    assert.doesNotThrow(own(f), 'a typed unavailable record is a complete outcome');
    f.evidence.osc78Evidence = { ...binding, reason: 'invented' };
    assert.throws(own(f), /Invalid OSC 7\/8 unavailable receipt binding/);
    f.evidence.osc78Evidence = { ...binding, counts: { 'inconclusive/supported': 1, 'supported/supported': 1 } };
    assert.throws(own(f), /Invalid OSC 7\/8 unavailable receipt binding/);
    f.evidence.osc78Evidence = binding;
    writeFileSync(join(f.output, 'lnk-run.json'), '{}');
    assert.throws(own(f), /retains graded artifacts/);
}));
