import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
import { includesOsc9Evidence, checkOsc9Evidence, osc9FirstPolicy } from './osc9-receipt.mjs';
import { osc9AdapterId } from './osc9-grading-collector.mjs';
import { osc9SuiteId } from './osc9-contracts.mjs';
import { layerChain } from './osc133-receipt.mjs';

const fixture = action => baseFixture(action, { routed: true, osc9: true });
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const rebind = (f, field, file) => { f.evidence.osc9Evidence[field] = hash(readFileSync(join(f.output, file))); };
const own = f => () => checkOsc9Evidence(f.output, f.evidence, f.context.runId, f.context);
const refusal = error => !(error instanceof TypeError);
const predecessors = layerChain.map(([name]) => name);

test('a complete run carries the OSC 9 suffix: pinned-preserved counts, identity, focus record and the layered derivation rederive without the collector', () => fixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.equal(f.finish().schemaVersion, 57);
    assert.deepEqual(f.evidence.osc9Evidence.counts, { 'inconclusive/supported': 2 });
    assert.equal(f.context.osc9AdapterId, osc9AdapterId); assert.equal(f.context.osc9SuiteId, osc9SuiteId);
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => fixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v47' };
    assert.equal(osc9FirstPolicy, 51);
    assert.equal(includesOsc9Evidence(f.output, f.evidence, f.context), true);
    assert.equal(includesOsc9Evidence(empty, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v51' }), true, 'the current policy always carries the suffix');
    assert.equal(includesOsc9Evidence(empty, {}, earlier), false);
    assert.equal(includesOsc9Evidence(empty, {}, { ...earlier, osc9SuiteId: 'x' }), true);
    assert.equal(includesOsc9Evidence(empty, { osc9Evidence: {} }, earlier), true);
    assert.equal(includesOsc9Evidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.osc9Evidence; }],
    ['changed suite identity', f => { f.evidence.osc9Evidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.osc9Evidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.osc9Evidence.counts = { 'inconclusive/unsupported': 2 }; }],
    ['a stale observation digest', f => { f.evidence.osc9Evidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.osc9Evidence.runSha256 = '0'.repeat(64); }],
    ['a stale focus digest', f => { f.evidence.osc9Evidence.focusSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.osc9Evidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.osc9Evidence.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.osc9ContractsVersion = 2; }],
    ['a missing adapter declaration', f => { delete f.context.osc9AdapterId; }],
    ['a changed adapter bundle declaration', f => { f.context.osc9BundleSha256 = '0'.repeat(64); }],
    ...predecessors.flatMap(name => [
        [`a missing ${name} predecessor`, f => { delete f.evidence[`${name}Evidence`]; }],
        [`a changed ${name} predecessor bundle`, f => { f.context[`${name}BundleSha256`] = '0'.repeat(64); }],
    ]),
]) test('the OSC 9 receipt refuses ' + label, () => fixture(f => { mutate(f); f.write('context.json', f.context); assert.throws(own(f), refusal); }));

test('the OSC 9 receipt refuses an adapter that is not the retained layered derivation', () => fixture(f => {
    writeFileSync(join(f.output, 'collector-osn.mjs'), readFileSync(join(f.output, 'collector-osn.mjs'), 'utf8') + '\n// edited\n');
    rebind(f, 'adapterBundleSha256', 'collector-osn.mjs');
    f.context.osc9BundleSha256 = f.evidence.osc9Evidence.adapterBundleSha256; f.write('context.json', f.context);
    assert.throws(own(f), /not the retained layered derivation/);
}));

test('the OSC 9 receipt refuses a changed run log, a changed report and a changed focus record even when rehashed', () => fixture(f => {
    const run = JSON.parse(readFileSync(join(f.output, 'osn-run.json')));
    run.pinned[1].termless.pass = true;
    writeFileSync(join(f.output, 'osn-run.json'), JSON.stringify(run, null, 2) + '\n'); rebind(f, 'runSha256', 'osn-run.json');
    assert.throws(own(f), refusal, 'run log');
}));

test('the OSC 9 receipt refuses a report that no longer equals its rederivation', () => fixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'osn-observations.json')));
    report.cases[1].effectContract.outcome = 'unsupported';
    writeFileSync(join(f.output, 'osn-observations.json'), JSON.stringify(report, null, 2) + '\n'); rebind(f, 'sha256', 'osn-observations.json');
    f.evidence.osc9Evidence.counts = { 'inconclusive/unsupported': 1, 'inconclusive/supported': 1 };
    assert.throws(f.finish, /observations do not match retained evidence/);
}));

test('the OSC 9 receipt refuses a focus record that differs from the journaled actions', () => fixture(f => {
    const focus = JSON.parse(readFileSync(join(f.output, 'osn-focus.json')));
    focus[0].exitCode = 1;
    writeFileSync(join(f.output, 'osn-focus.json'), JSON.stringify(focus, null, 2) + '\n'); rebind(f, 'focusSha256', 'osn-focus.json');
    assert.throws(own(f), /focus record differs/);
    rmSync(join(f.output, 'osn-focus.json'));
    assert.throws(own(f), refusal, 'a missing focus record');
}));

test('the OSC 9 receipt refuses edited, truncated, emptied and missing journals', () => fixture(f => {
    const journal = join(f.output, 'osn-snapshots.jsonl'), lines = readFileSync(journal, 'utf8').trimEnd().split('\n');
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

test('the OSC 9 receipt refuses retained files that are symlinks', () => fixture(f => {
    rmSync(join(f.output, 'osn-run.json'));
    writeFileSync(join(f.output, 'osn-run-target.json'), '{}');
    symlinkSync(join(f.output, 'osn-run-target.json'), join(f.output, 'osn-run.json'));
    assert.throws(own(f), /not a regular file/);
}));

test('typed initial unavailability is retained, while partial artifacts cannot be hidden', () => fixture(f => {
    const marker = { schemaVersion: 1, runId: f.context.runId, suiteId: osc9SuiteId, status: 'unavailable', reason: 'paneMetadataUnavailable', plannedCases: 2 };
    const bytes = Buffer.from(JSON.stringify(marker) + '\n');
    for (const name of ['osn-run.json', 'osn-observations.json', 'osn-snapshots.jsonl', 'osn-focus.json']) rmSync(join(f.output, name));
    writeFileSync(join(f.output, 'osn-unavailable.json'), bytes);
    Object.assign(f.evidence.osc9Evidence, { file: 'osn-unavailable.json', status: 'unavailable', reason: marker.reason, counts: {}, sha256: hash(bytes) });
    assert.doesNotThrow(own(f));
    writeFileSync(join(f.output, 'osn-snapshots.jsonl'), 'partial');
    assert.throws(own(f), /partial|retains graded/);
}));
