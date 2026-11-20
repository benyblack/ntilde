import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
import { includesPclEvidence, checkPclEvidence, pclFirstPolicy, pclCounts } from './pcl-receipt.mjs';
import { pclAdapterId } from './pcl-grading-collector.mjs';
import { pclSuiteId } from './pcl-contracts.mjs';
import { layerChain } from './osc133-receipt.mjs';

const fixture = action => baseFixture(action, { routed: true, pcl: true });
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const own = f => () => checkPclEvidence(f.output, f.evidence, f.context.runId, f.context);
const predecessors = layerChain.map(([name]) => name);

test('a complete run carries the pointer-color suffix: pinned-inconclusive counts, identity and the layered derivation rederive without the collector', () => fixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.doesNotThrow(() => f.finish());
    assert.deepEqual(f.evidence.pclEvidence.counts, { inconclusive: 2 });
    assert.equal(f.context.pclAdapterId, pclAdapterId); assert.equal(f.context.pclSuiteId, pclSuiteId);
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => fixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v47' };
    const first = `readonly-owned-source-graph-v${pclFirstPolicy}`, before = `readonly-owned-source-graph-v${pclFirstPolicy - 1}`;
    assert.equal(includesPclEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesPclEvidence(empty, {}, { sourceSnapshotPolicy: first }), true, 'the first pointer-color policy always carries the suffix');
    assert.equal(includesPclEvidence(empty, {}, { sourceSnapshotPolicy: before }), false, 'the previous policy does not');
    assert.equal(includesPclEvidence(empty, {}, earlier), false);
    assert.equal(includesPclEvidence(empty, {}, { ...earlier, pclSuiteId: 'x' }), true);
    assert.equal(includesPclEvidence(empty, { pclEvidence: {} }, earlier), true);
    assert.equal(includesPclEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

test('the counts are the pinned verdicts of the report, never the effect verdicts', () => fixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'pcl-observations.json')));
    assert.deepEqual(pclCounts(report), { inconclusive: 2 });
    report.cases[0].conclusion.outcome = 'unsupported';
    assert.deepEqual(pclCounts(report), { inconclusive: 1, unsupported: 1 });
    assert.ok(JSON.parse(readFileSync(join(f.output, 'pcl-observations.json'))).cases.every(item => item.effectContract.outcome === 'unsupported'));
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.pclEvidence; }],
    ['changed suite identity', f => { f.evidence.pclEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.pclEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.pclEvidence.counts = { supported: 2 }; }],
    ['a stale observation digest', f => { f.evidence.pclEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.pclEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.pclEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.pclEvidence.contractsVersion = 2; }],
    ['a missing predecessor layer', f => { delete f.evidence.lrMarginsEvidence; }],
    ['a symlinked observation file', f => { rmSync(join(f.output, 'pcl-observations.json')); symlinkSync('/etc/hosts', join(f.output, 'pcl-observations.json')); }],
]) test('the pointer-color receipt refuses ' + label, () => fixture(f => { mutate(f); assert.throws(own(f), refusal); }));

function refusal(error) { return !(error instanceof TypeError); }
