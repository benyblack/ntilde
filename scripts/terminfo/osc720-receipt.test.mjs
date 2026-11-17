import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
import { includesOsc720Evidence, checkOsc720Evidence, osc720FirstPolicy } from './osc720-receipt.mjs';
import { osc720AdapterId } from './osc720-grading-collector.mjs';
import { osc720SuiteId } from './osc720-contracts.mjs';
import { layerChain } from './osc133-receipt.mjs';

const fixture = action => baseFixture(action, { routed: true, osc720: true });
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const rebind = (f, field, file) => { f.evidence.osc720Evidence[field] = hash(readFileSync(join(f.output, file))); };
const own = f => () => checkOsc720Evidence(f.output, f.evidence, f.context.runId, f.context);
const refusal = error => !(error instanceof TypeError);
const predecessors = layerChain.map(([name]) => name);

test('a complete run carries the OSC 720 suffix: pinned-preserved counts, identity, action record and the layered derivation rederive without the collector', () => fixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.equal(f.finish().schemaVersion, 74);
    assert.deepEqual(f.evidence.osc720Evidence.counts, { 'inconclusive/unsupported': 1 });
    assert.equal(f.context.osc720AdapterId, osc720AdapterId); assert.equal(f.context.osc720SuiteId, osc720SuiteId);
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => fixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v47' };
    assert.equal(osc720FirstPolicy, 67);
    assert.equal(includesOsc720Evidence(f.output, f.evidence, f.context), true);
    assert.equal(includesOsc720Evidence(empty, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v67' }), true, 'the current policy always carries the suffix');
    assert.equal(includesOsc720Evidence(empty, {}, earlier), false);
    assert.equal(includesOsc720Evidence(empty, {}, { ...earlier, osc720SuiteId: 'x' }), true);
    assert.equal(includesOsc720Evidence(empty, { osc720Evidence: {} }, earlier), true);
    assert.equal(includesOsc720Evidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.osc720Evidence; }],
    ['changed suite identity', f => { f.evidence.osc720Evidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.osc720Evidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.osc720Evidence.counts = { 'inconclusive/supported': 1 }; }],
    ['a stale observation digest', f => { f.evidence.osc720Evidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.osc720Evidence.runSha256 = '0'.repeat(64); }],
    ['a stale action digest', f => { f.evidence.osc720Evidence.actionsSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.osc720Evidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.osc720Evidence.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.osc720ContractsVersion = 2; }],
    ['a missing adapter declaration', f => { delete f.context.osc720AdapterId; }],
    ['a changed adapter bundle declaration', f => { f.context.osc720BundleSha256 = '0'.repeat(64); }],
    ...predecessors.flatMap(name => [
        [`a missing ${name} predecessor`, f => { delete f.evidence[`${name}Evidence`]; }],
        [`a changed ${name} predecessor bundle`, f => { f.context[`${name}BundleSha256`] = '0'.repeat(64); }],
    ]),
]) test('the OSC 720 receipt refuses ' + label, () => fixture(f => { mutate(f); f.write('context.json', f.context); assert.throws(own(f), refusal); }));

test('the OSC 720 receipt refuses an adapter that is not the retained layered derivation', () => fixture(f => {
    writeFileSync(join(f.output, 'collector-ovu.mjs'), readFileSync(join(f.output, 'collector-ovu.mjs'), 'utf8') + '\n// edited\n');
    rebind(f, 'adapterBundleSha256', 'collector-ovu.mjs');
    f.context.osc720BundleSha256 = f.evidence.osc720Evidence.adapterBundleSha256; f.write('context.json', f.context);
    assert.throws(own(f), /not the retained layered derivation/);
}));

test('the OSC 720 receipt refuses a changed run log even when rehashed', () => fixture(f => {
    const run = JSON.parse(readFileSync(join(f.output, 'ovu-run.json')));
    run.pinned.termless.pass = true;
    writeFileSync(join(f.output, 'ovu-run.json'), JSON.stringify(run, null, 2) + '\n'); rebind(f, 'runSha256', 'ovu-run.json');
    assert.throws(own(f), refusal, 'run log');
}));

test('the OSC 720 receipt refuses a report that no longer equals its rederivation', () => fixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'ovu-observations.json')));
    report.cases[0].effectContract.outcome = 'supported';
    writeFileSync(join(f.output, 'ovu-observations.json'), JSON.stringify(report, null, 2) + '\n'); rebind(f, 'sha256', 'ovu-observations.json');
    f.evidence.osc720Evidence.counts = { 'inconclusive/supported': 1 };
    assert.throws(f.finish, /observations do not match retained evidence/);
}));

test('the OSC 720 receipt refuses a action record that differs from the journaled actions', () => fixture(f => {
    const actions = JSON.parse(readFileSync(join(f.output, 'ovu-actions.json')));
    actions[0].notches[0].handled = false;
    writeFileSync(join(f.output, 'ovu-actions.json'), JSON.stringify(actions, null, 2) + '\n'); rebind(f, 'actionsSha256', 'ovu-actions.json');
    assert.throws(own(f), /action record differs/);
    rmSync(join(f.output, 'ovu-actions.json'));
    assert.throws(own(f), refusal, 'a missing action record');
}));

test('the OSC 720 receipt refuses edited, truncated, emptied and missing journals', () => fixture(f => {
    const journal = join(f.output, 'ovu-snapshots.jsonl'), lines = readFileSync(journal, 'utf8').trimEnd().split('\n');
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

test('the OSC 720 receipt refuses retained files that are symlinks', () => fixture(f => {
    rmSync(join(f.output, 'ovu-run.json'));
    writeFileSync(join(f.output, 'ovu-run-target.json'), '{}');
    symlinkSync(join(f.output, 'ovu-run-target.json'), join(f.output, 'ovu-run.json'));
    assert.throws(own(f), /not a regular file/);
}));

test('typed initial unavailability is retained, while partial artifacts cannot be hidden', () => fixture(f => {
    const marker = { schemaVersion: 1, runId: f.context.runId, suiteId: osc720SuiteId, status: 'unavailable', reason: 'paneMetadataUnavailable', plannedCases: 1 };
    const bytes = Buffer.from(JSON.stringify(marker) + '\n');
    for (const name of ['ovu-run.json', 'ovu-observations.json', 'ovu-snapshots.jsonl', 'ovu-actions.json']) rmSync(join(f.output, name));
    writeFileSync(join(f.output, 'ovu-unavailable.json'), bytes);
    Object.assign(f.evidence.osc720Evidence, { file: 'ovu-unavailable.json', status: 'unavailable', reason: marker.reason, counts: {}, sha256: hash(bytes) });
    assert.doesNotThrow(own(f));
    writeFileSync(join(f.output, 'ovu-snapshots.jsonl'), 'partial');
    assert.throws(own(f), /partial|retains graded/);
}));
