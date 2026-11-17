import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
import { includesOsc3008Evidence, checkOsc3008Evidence, osc3008FirstPolicy } from './osc3008-receipt.mjs';
import { osc3008AdapterId } from './osc3008-grading-collector.mjs';
import { osc3008SuiteId } from './osc3008-contracts.mjs';
import { osc3008Counts } from './osc3008-grading.mjs';
import { layerChain } from './osc133-receipt.mjs';

const fixture = action => baseFixture(action, { routed: true, kcl: true, osc5: true, osc3008: true });
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const own = f => () => checkOsc3008Evidence(f.output, f.evidence, f.context.runId, f.context);
const predecessors = layerChain.map(([name]) => name);
const journalLines = f => readFileSync(join(f.output, 'ocx-snapshots.jsonl'), 'utf8').trim().split('\n');

test('a complete run carries the OSC 3008 suffix: counts, identity and the layered derivation rederive without the collector', () => fixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.doesNotThrow(() => f.finish());
    assert.deepEqual(f.evidence.osc3008Evidence.counts, { 'inconclusive/supported': 1 });
    assert.equal(f.context.osc3008AdapterId, osc3008AdapterId); assert.equal(f.context.osc3008SuiteId, osc3008SuiteId);
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => fixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v47' };
    const first = `readonly-owned-source-graph-v${osc3008FirstPolicy}`, before = `readonly-owned-source-graph-v${osc3008FirstPolicy - 1}`;
    assert.equal(includesOsc3008Evidence(f.output, f.evidence, f.context), true);
    assert.equal(includesOsc3008Evidence(empty, {}, { sourceSnapshotPolicy: first }), true, 'the first OSC 3008 policy always carries the suffix');
    assert.equal(includesOsc3008Evidence(empty, {}, { sourceSnapshotPolicy: before }), false, 'the previous policy does not');
    assert.equal(includesOsc3008Evidence(empty, {}, earlier), false);
    assert.equal(includesOsc3008Evidence(empty, {}, { ...earlier, osc3008SuiteId: 'x' }), true);
    assert.equal(includesOsc3008Evidence(empty, { osc3008Evidence: {} }, earlier), true);
    assert.equal(includesOsc3008Evidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

test('the counts are the preserved pinned verdict over the effect verdict', () => fixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'ocx-observations.json')));
    assert.deepEqual(osc3008Counts(report), { 'inconclusive/supported': 1 });
    report.cases[0].effectContract.outcome = 'unsupported';
    assert.deepEqual(osc3008Counts(report), { 'inconclusive/unsupported': 1 });
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.osc3008Evidence; }],
    ['changed suite identity', f => { f.evidence.osc3008Evidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.osc3008Evidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.osc3008Evidence.counts = { 'inconclusive/unsupported': 1 }; }],
    ['a stale observation digest', f => { f.evidence.osc3008Evidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.osc3008Evidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.osc3008Evidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.osc3008Evidence.contractsVersion = 2; }],
    ['a missing predecessor layer', f => { delete f.evidence.lrMarginsEvidence; }],
    ['a changed adapter file', f => { writeFileSync(join(f.output, 'collector-ocx.mjs'), 'modified'); }],
    ['a symlinked observation file', f => { rmSync(join(f.output, 'ocx-observations.json')); symlinkSync('/etc/hosts', join(f.output, 'ocx-observations.json')); }],
    ['an emptied journal', f => { writeFileSync(join(f.output, 'ocx-snapshots.jsonl'), ''); }],
    ['a truncated journal', f => { writeFileSync(join(f.output, 'ocx-snapshots.jsonl'), journalLines(f).slice(0, -1).join('\n') + '\n'); }],
]) test('the OSC 3008 receipt refuses ' + label, () => fixture(f => { mutate(f); assert.throws(own(f), refusal); }));

test('a tampered stored context in the journal no longer rederives the report', () => fixture(f => {
    const lines = journalLines(f), index = lines.findIndex(line => JSON.parse(line).kind === 'ocx-read'), frame = JSON.parse(lines[index]);
    frame.before.metadata.buffer.contexts.stack.push({ id: 'forged' });
    frame.after = structuredClone(frame.before);
    lines[index] = JSON.stringify(frame);
    writeFileSync(join(f.output, 'ocx-snapshots.jsonl'), lines.join('\n') + '\n');
    assert.throws(own(f), refusal);
}));

test('a typed initial unavailability is admitted only without retained partial observations', () => fixture(f => {
    const marker = { schemaVersion: 1, runId: f.context.runId, suiteId: osc3008SuiteId, status: 'unavailable', reason: 'paneMetadataUnavailable', plannedCases: 1 };
    const bytes = Buffer.from(JSON.stringify(marker) + '\n');
    for (const name of ['ocx-observations.json', 'ocx-run.json', 'ocx-snapshots.jsonl']) rmSync(join(f.output, name));
    writeFileSync(join(f.output, 'ocx-unavailable.json'), bytes);
    f.evidence.osc3008Evidence = { runId: f.context.runId, suiteId: osc3008SuiteId, file: 'ocx-unavailable.json', sha256: hash(bytes), status: 'unavailable', reason: 'paneMetadataUnavailable', counts: {},
        adapterBundleSha256: f.context.osc3008BundleSha256, contractsVersion: 1, provisionalUntil: 'cell-receipt.json' };
    assert.doesNotThrow(own(f));
    writeFileSync(join(f.output, 'ocx-run.json'), '{}\n');
    assert.throws(own(f), refusal);
    rmSync(join(f.output, 'ocx-run.json'));
    f.evidence.osc3008Evidence.reason = 'somethingElse';
    assert.throws(own(f), refusal);
}));

function refusal(error) { return !(error instanceof TypeError); }
