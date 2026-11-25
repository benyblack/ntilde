import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
import { includesDslEvidence, checkDslEvidence, dslFirstPolicy, dslCounts } from './dsl-receipt.mjs';
import { dslAdapterId } from './dsl-grading-collector.mjs';
import { dslSuiteId } from './dsl-contracts.mjs';
import { layerChain } from './osc133-receipt.mjs';

const fixture = action => baseFixture(action, { routed: true, dsl: true });
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const own = f => () => checkDslEvidence(f.output, f.evidence, f.context.runId, f.context);
const predecessors = layerChain.map(([name]) => name);

test('a complete run carries the DECSCLM suffix: pinned-inconclusive counts, identity and the layered derivation rederive without the collector', () => fixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.doesNotThrow(() => f.finish());
    assert.deepEqual(f.evidence.dslEvidence.counts, { inconclusive: 1 });
    assert.equal(f.context.dslAdapterId, dslAdapterId); assert.equal(f.context.dslSuiteId, dslSuiteId);
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => fixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v47' };
    const first = `readonly-owned-source-graph-v${dslFirstPolicy}`, before = `readonly-owned-source-graph-v${dslFirstPolicy - 1}`;
    assert.equal(includesDslEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesDslEvidence(empty, {}, { sourceSnapshotPolicy: first }), true, 'the first DECSCLM policy always carries the suffix');
    assert.equal(includesDslEvidence(empty, {}, { sourceSnapshotPolicy: before }), false, 'the previous policy does not');
    assert.equal(includesDslEvidence(empty, {}, earlier), false);
    assert.equal(includesDslEvidence(empty, {}, { ...earlier, dslSuiteId: 'x' }), true);
    assert.equal(includesDslEvidence(empty, { dslEvidence: {} }, earlier), true);
    assert.equal(includesDslEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

test('the counts are the pinned verdicts of the report, never the effect verdicts', () => fixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'dsl-observations.json')));
    assert.deepEqual(dslCounts(report), { inconclusive: 1 });
    report.cases[0].conclusion.outcome = 'unsupported';
    assert.deepEqual(dslCounts(report), { unsupported: 1 });
    assert.ok(JSON.parse(readFileSync(join(f.output, 'dsl-observations.json'))).cases.every(item => item.effectContract.outcome === 'unsupported'));
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.dslEvidence; }],
    ['changed suite identity', f => { f.evidence.dslEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.dslEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.dslEvidence.counts = { supported: 1 }; }],
    ['a stale observation digest', f => { f.evidence.dslEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.dslEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.dslEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.dslEvidence.contractsVersion = 2; }],
    ['a missing predecessor layer', f => { delete f.evidence.lrMarginsEvidence; }],
    ['a symlinked observation file', f => { rmSync(join(f.output, 'dsl-observations.json')); symlinkSync('/etc/hosts', join(f.output, 'dsl-observations.json')); }],
]) test('the DECSCLM receipt refuses ' + label, () => fixture(f => { mutate(f); assert.throws(own(f), refusal); }));

function refusal(error) { return !(error instanceof TypeError); }
