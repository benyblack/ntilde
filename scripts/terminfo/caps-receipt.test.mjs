import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
import { includesCapsEvidence, checkCapsEvidence, capsFirstPolicy, capsCounts } from './caps-receipt.mjs';
import { capsAdapterId } from './caps-grading-collector.mjs';
import { capsSuiteId } from './caps-contracts.mjs';
import { layerChain } from './osc133-receipt.mjs';

const fixture = action => baseFixture(action, { routed: true, caps: true });
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const own = f => () => checkCapsEvidence(f.output, f.evidence, f.context.runId, f.context);
const predecessors = layerChain.map(([name]) => name);

test('a complete run carries the feature-reporting suffix: pinned-supported counts, identity and the layered derivation rederive without the collector', () => fixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.doesNotThrow(() => f.finish());
    assert.deepEqual(f.evidence.capsEvidence.counts, { supported: 1 });
    assert.equal(f.context.capsAdapterId, capsAdapterId); assert.equal(f.context.capsSuiteId, capsSuiteId);
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => fixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v47' };
    const first = `readonly-owned-source-graph-v${capsFirstPolicy}`, before = `readonly-owned-source-graph-v${capsFirstPolicy - 1}`;
    assert.equal(includesCapsEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesCapsEvidence(empty, {}, { sourceSnapshotPolicy: first }), true, 'the first feature-reporting policy always carries the suffix');
    assert.equal(includesCapsEvidence(empty, {}, { sourceSnapshotPolicy: before }), false, 'the previous policy does not');
    assert.equal(includesCapsEvidence(empty, {}, earlier), false);
    assert.equal(includesCapsEvidence(empty, {}, { ...earlier, capsSuiteId: 'x' }), true);
    assert.equal(includesCapsEvidence(empty, { capsEvidence: {} }, earlier), true);
    assert.equal(includesCapsEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

test('the counts are the pinned verdicts of the report, never the effect verdicts', () => fixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'caps-observations.json')));
    assert.deepEqual(capsCounts(report), { supported: 1 });
    report.cases[0].conclusion.outcome = 'inconclusive';
    assert.deepEqual(capsCounts(report), { inconclusive: 1 });
    assert.ok(JSON.parse(readFileSync(join(f.output, 'caps-observations.json'))).cases.every(item => item.effectContract.outcome === 'supported'));
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.capsEvidence; }],
    ['changed suite identity', f => { f.evidence.capsEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.capsEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.capsEvidence.counts = { inconclusive: 1 }; }],
    ['a stale observation digest', f => { f.evidence.capsEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.capsEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.capsEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.capsEvidence.contractsVersion = 2; }],
    ['a missing predecessor layer', f => { delete f.evidence.lrMarginsEvidence; }],
    ['a symlinked observation file', f => { rmSync(join(f.output, 'caps-observations.json')); symlinkSync('/etc/hosts', join(f.output, 'caps-observations.json')); }],
]) test('the feature-reporting receipt refuses ' + label, () => fixture(f => { mutate(f); assert.throws(own(f), refusal); }));

function refusal(error) { return !(error instanceof TypeError); }
