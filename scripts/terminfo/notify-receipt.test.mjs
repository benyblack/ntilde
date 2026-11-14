import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
import { includesNotifyEvidence, checkNotifyEvidence, notifyFirstPolicy } from './notify-receipt.mjs';
import { notifyAdapterId } from './notify-grading-collector.mjs';
import { notifySuiteId } from './notify-contracts.mjs';
import { layerChain } from './osc133-receipt.mjs';

const fixture = action => baseFixture(action, { routed: true, notify: true });
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const rebind = (f, field, file) => { f.evidence.notifyEvidence[field] = hash(readFileSync(join(f.output, file))); };
const own = f => () => checkNotifyEvidence(f.output, f.evidence, f.context.runId, f.context);
const refusal = error => !(error instanceof TypeError);
const predecessors = layerChain.map(([name]) => name);

test('a complete run carries the notification suffix: pinned-preserved counts, identity, focus record and the layered derivation rederive without the collector', () => fixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.equal(f.finish().schemaVersion, 59);
    assert.deepEqual(f.evidence.notifyEvidence.counts, { 'inconclusive/supported': 1, 'stock-and-query-witness/supported': 1 });
    assert.equal(f.context.notifyAdapterId, notifyAdapterId); assert.equal(f.context.notifySuiteId, notifySuiteId);
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => fixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v47' };
    assert.equal(notifyFirstPolicy, 53);
    assert.equal(includesNotifyEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesNotifyEvidence(empty, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v53' }), true, 'the current policy always carries the suffix');
    assert.equal(includesNotifyEvidence(empty, {}, earlier), false);
    assert.equal(includesNotifyEvidence(empty, {}, { ...earlier, notifySuiteId: 'x' }), true);
    assert.equal(includesNotifyEvidence(empty, { notifyEvidence: {} }, earlier), true);
    assert.equal(includesNotifyEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.notifyEvidence; }],
    ['changed suite identity', f => { f.evidence.notifyEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.notifyEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.notifyEvidence.counts = { 'inconclusive/unsupported': 1, 'stock-and-query-witness/unsupported': 1 }; }],
    ['a stale observation digest', f => { f.evidence.notifyEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.notifyEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale focus digest', f => { f.evidence.notifyEvidence.focusSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.notifyEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.notifyEvidence.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.notifyContractsVersion = 2; }],
    ['a missing adapter declaration', f => { delete f.context.notifyAdapterId; }],
    ['a changed adapter bundle declaration', f => { f.context.notifyBundleSha256 = '0'.repeat(64); }],
    ...predecessors.flatMap(name => [
        [`a missing ${name} predecessor`, f => { delete f.evidence[`${name}Evidence`]; }],
        [`a changed ${name} predecessor bundle`, f => { f.context[`${name}BundleSha256`] = '0'.repeat(64); }],
    ]),
]) test('the notification receipt refuses ' + label, () => fixture(f => { mutate(f); f.write('context.json', f.context); assert.throws(own(f), refusal); }));

test('the notification receipt refuses an adapter that is not the retained layered derivation', () => fixture(f => {
    writeFileSync(join(f.output, 'collector-ntf.mjs'), readFileSync(join(f.output, 'collector-ntf.mjs'), 'utf8') + '\n// edited\n');
    rebind(f, 'adapterBundleSha256', 'collector-ntf.mjs');
    f.context.notifyBundleSha256 = f.evidence.notifyEvidence.adapterBundleSha256; f.write('context.json', f.context);
    assert.throws(own(f), /not the retained layered derivation/);
}));

test('the notification receipt refuses a changed run log, a changed report and a changed focus record even when rehashed', () => fixture(f => {
    const run = JSON.parse(readFileSync(join(f.output, 'ntf-run.json')));
    run.pinned[0].terminal.pass = true;
    writeFileSync(join(f.output, 'ntf-run.json'), JSON.stringify(run, null, 2) + '\n'); rebind(f, 'runSha256', 'ntf-run.json');
    assert.throws(own(f), refusal, 'run log');
}));

test('the notification receipt refuses a report that no longer equals its rederivation', () => fixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'ntf-observations.json')));
    report.cases[1].effectContract.outcome = 'unsupported';
    writeFileSync(join(f.output, 'ntf-observations.json'), JSON.stringify(report, null, 2) + '\n'); rebind(f, 'sha256', 'ntf-observations.json');
    f.evidence.notifyEvidence.counts = { 'inconclusive/supported': 1, 'stock-and-query-witness/unsupported': 1 };
    assert.throws(f.finish, /observations do not match retained evidence/);
}));

test('the notification receipt refuses a focus record that differs from the journaled actions', () => fixture(f => {
    const focus = JSON.parse(readFileSync(join(f.output, 'ntf-focus.json')));
    focus[0].exitCode = 1;
    writeFileSync(join(f.output, 'ntf-focus.json'), JSON.stringify(focus, null, 2) + '\n'); rebind(f, 'focusSha256', 'ntf-focus.json');
    assert.throws(own(f), /focus record differs/);
    rmSync(join(f.output, 'ntf-focus.json'));
    assert.throws(own(f), refusal, 'a missing focus record');
}));

test('the notification receipt refuses edited, truncated, emptied and missing journals', () => fixture(f => {
    const journal = join(f.output, 'ntf-snapshots.jsonl'), lines = readFileSync(journal, 'utf8').trimEnd().split('\n');
    const edited = JSON.parse(lines[7]); edited.before.screen.lines[1] = 'edited-in-the-journal';
    writeFileSync(journal, [...lines.slice(0, 7), JSON.stringify(edited), ...lines.slice(8)].join('\n') + '\n');
    assert.throws(own(f), refusal, 'an edited frame');
    writeFileSync(journal, lines.slice(0, -1).join('\n') + '\n');
    assert.throws(own(f), refusal, 'a truncated journal');
    writeFileSync(journal, '');
    assert.throws(own(f), /journal is empty/, 'an emptied journal');
    rmSync(journal);
    assert.throws(own(f), refusal, 'a missing journal');
}));

test('the notification receipt refuses retained files that are symlinks', () => fixture(f => {
    rmSync(join(f.output, 'ntf-run.json'));
    writeFileSync(join(f.output, 'ntf-run-target.json'), '{}');
    symlinkSync(join(f.output, 'ntf-run-target.json'), join(f.output, 'ntf-run.json'));
    assert.throws(own(f), /not a regular file/);
}));

test('typed initial unavailability is retained, while partial artifacts cannot be hidden', () => fixture(f => {
    const marker = { schemaVersion: 1, runId: f.context.runId, suiteId: notifySuiteId, status: 'unavailable', reason: 'paneMetadataUnavailable', plannedCases: 2 };
    const bytes = Buffer.from(JSON.stringify(marker) + '\n');
    for (const name of ['ntf-run.json', 'ntf-observations.json', 'ntf-snapshots.jsonl', 'ntf-focus.json']) rmSync(join(f.output, name));
    writeFileSync(join(f.output, 'ntf-unavailable.json'), bytes);
    Object.assign(f.evidence.notifyEvidence, { file: 'ntf-unavailable.json', status: 'unavailable', reason: marker.reason, counts: {}, sha256: hash(bytes) });
    assert.doesNotThrow(own(f));
    writeFileSync(join(f.output, 'ntf-snapshots.jsonl'), 'partial');
    assert.throws(own(f), /partial|retains graded/);
}));
