import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
import { includesOsc5Evidence, checkOsc5Evidence, osc5FirstPolicy, osc5Counts } from './osc5-receipt.mjs';
import { osc5AdapterId } from './osc5-grading-collector.mjs';
import { osc5SuiteId } from './osc5-contracts.mjs';
import { layerChain } from './osc133-receipt.mjs';

const fixture = action => baseFixture(action, { routed: true, kcl: true, osc5: true });
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const rebind = (f, field, file) => { f.evidence.osc5Evidence[field] = hash(readFileSync(join(f.output, file))); };
const own = f => () => checkOsc5Evidence(f.output, f.evidence, f.context.runId, f.context);
const predecessors = layerChain.map(([name]) => name);

test('a complete run carries the special-color suffix: pinned-supported counts, identity and the layered derivation rederive without the collector', () => fixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.doesNotThrow(() => f.finish());
    assert.deepEqual(f.evidence.osc5Evidence.counts, { supported: 1 });
    assert.equal(f.context.osc5AdapterId, osc5AdapterId); assert.equal(f.context.osc5SuiteId, osc5SuiteId);
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => fixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v47' };
    const first = `readonly-owned-source-graph-v${osc5FirstPolicy}`, before = `readonly-owned-source-graph-v${osc5FirstPolicy - 1}`;
    assert.equal(includesOsc5Evidence(f.output, f.evidence, f.context), true);
    assert.equal(includesOsc5Evidence(empty, {}, { sourceSnapshotPolicy: first }), true, 'the first special-color policy always carries the suffix');
    assert.equal(includesOsc5Evidence(empty, {}, { sourceSnapshotPolicy: before }), false, 'the previous policy does not');
    assert.equal(includesOsc5Evidence(empty, {}, earlier), false);
    assert.equal(includesOsc5Evidence(empty, {}, { ...earlier, osc5SuiteId: 'x' }), true);
    assert.equal(includesOsc5Evidence(empty, { osc5Evidence: {} }, earlier), true);
    assert.equal(includesOsc5Evidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

test('the counts are the pinned verdicts of the report, never the effect verdicts', () => fixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'osc5-observations.json')));
    assert.deepEqual(osc5Counts(report), { supported: 1 });
    report.cases[0].conclusion.outcome = 'inconclusive';
    assert.deepEqual(osc5Counts(report), { inconclusive: 1 });
    assert.ok(JSON.parse(readFileSync(join(f.output, 'osc5-observations.json'))).cases.every(item => item.effectContract.outcome === 'supported'));
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.osc5Evidence; }],
    ['changed suite identity', f => { f.evidence.osc5Evidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.osc5Evidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.osc5Evidence.counts = { inconclusive: 1 }; }],
    ['a stale observation digest', f => { f.evidence.osc5Evidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.osc5Evidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.osc5Evidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.osc5Evidence.contractsVersion = 2; }],
    ['a missing predecessor layer', f => { delete f.evidence.lrMarginsEvidence; }],
    ['a symlinked observation file', f => { rmSync(join(f.output, 'osc5-observations.json')); symlinkSync('/etc/hosts', join(f.output, 'osc5-observations.json')); }],
]) test('the special-color receipt refuses ' + label, () => fixture(f => { mutate(f); assert.throws(own(f), refusal); }));

function refusal(error) { return !(error instanceof TypeError); }
