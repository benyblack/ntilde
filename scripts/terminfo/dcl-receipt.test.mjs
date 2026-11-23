import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
import { includesDclEvidence, checkDclEvidence, dclFirstPolicy, dclCounts } from './dcl-receipt.mjs';
import { dclAdapterId } from './dcl-grading-collector.mjs';
import { dclSuiteId } from './dcl-contracts.mjs';
import { layerChain } from './osc133-receipt.mjs';

const fixture = action => baseFixture(action, { routed: true, dcl: true });
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const own = f => () => checkDclEvidence(f.output, f.evidence, f.context.runId, f.context);
const predecessors = layerChain.map(([name]) => name);

test('a complete run carries the DECCOLM suffix: pinned-inconclusive counts, identity and the layered derivation rederive without the collector', () => fixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.doesNotThrow(() => f.finish());
    assert.deepEqual(f.evidence.dclEvidence.counts, { inconclusive: 1 });
    assert.equal(f.context.dclAdapterId, dclAdapterId); assert.equal(f.context.dclSuiteId, dclSuiteId);
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => fixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v47' };
    const first = `readonly-owned-source-graph-v${dclFirstPolicy}`, before = `readonly-owned-source-graph-v${dclFirstPolicy - 1}`;
    assert.equal(includesDclEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesDclEvidence(empty, {}, { sourceSnapshotPolicy: first }), true, 'the first DECCOLM policy always carries the suffix');
    assert.equal(includesDclEvidence(empty, {}, { sourceSnapshotPolicy: before }), false, 'the previous policy does not');
    assert.equal(includesDclEvidence(empty, {}, earlier), false);
    assert.equal(includesDclEvidence(empty, {}, { ...earlier, dclSuiteId: 'x' }), true);
    assert.equal(includesDclEvidence(empty, { dclEvidence: {} }, earlier), true);
    assert.equal(includesDclEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

test('the counts are the pinned verdicts of the report, never the effect verdicts', () => fixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'dcl-observations.json')));
    assert.deepEqual(dclCounts(report), { inconclusive: 1 });
    report.cases[0].conclusion.outcome = 'unsupported';
    assert.deepEqual(dclCounts(report), { unsupported: 1 });
    assert.ok(JSON.parse(readFileSync(join(f.output, 'dcl-observations.json'))).cases.every(item => item.effectContract.outcome === 'unsupported'));
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.dclEvidence; }],
    ['changed suite identity', f => { f.evidence.dclEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.dclEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.dclEvidence.counts = { supported: 1 }; }],
    ['a stale observation digest', f => { f.evidence.dclEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.dclEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.dclEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.dclEvidence.contractsVersion = 2; }],
    ['a missing predecessor layer', f => { delete f.evidence.lrMarginsEvidence; }],
    ['a symlinked observation file', f => { rmSync(join(f.output, 'dcl-observations.json')); symlinkSync('/etc/hosts', join(f.output, 'dcl-observations.json')); }],
]) test('the DECCOLM receipt refuses ' + label, () => fixture(f => { mutate(f); assert.throws(own(f), refusal); }));

function refusal(error) { return !(error instanceof TypeError); }
