import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
import { includesXcsEvidence, checkXcsEvidence, xcsFirstPolicy, xcsCounts } from './xcs-receipt.mjs';
import { xcsAdapterId } from './xcs-grading-collector.mjs';
import { xcsSuiteId } from './xcs-contracts.mjs';
import { layerChain } from './osc133-receipt.mjs';

const fixture = action => baseFixture(action, { routed: true, xcs: true });
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const rebind = (f, field, file) => { f.evidence.xcsEvidence[field] = hash(readFileSync(join(f.output, file))); };
const own = f => () => checkXcsEvidence(f.output, f.evidence, f.context.runId, f.context);
const refusal = error => !(error instanceof TypeError);
const predecessors = layerChain.map(([name]) => name);

test('a complete run carries the xterm color-stack suffix: pinned-inconclusive counts, identity and the layered derivation rederive without the collector', () => fixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.equal(f.finish().schemaVersion, 62);
    assert.deepEqual(f.evidence.xcsEvidence.counts, { inconclusive: 2 });
    assert.equal(f.context.xcsAdapterId, xcsAdapterId); assert.equal(f.context.xcsSuiteId, xcsSuiteId);
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => fixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v47' };
    assert.equal(xcsFirstPolicy, 56);
    assert.equal(includesXcsEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesXcsEvidence(empty, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v56' }), true, 'the current policy always carries the suffix');
    assert.equal(includesXcsEvidence(empty, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v55' }), false, 'the device-query policy does not');
    assert.equal(includesXcsEvidence(empty, {}, earlier), false);
    assert.equal(includesXcsEvidence(empty, {}, { ...earlier, xcsSuiteId: 'x' }), true);
    assert.equal(includesXcsEvidence(empty, { xcsEvidence: {} }, earlier), true);
    assert.equal(includesXcsEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

test('the counts are the pinned verdicts of the report, never the effect verdicts', () => fixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'xcs-observations.json')));
    assert.deepEqual(xcsCounts(report), { inconclusive: 2 });
    assert.ok(report.cases.every(item => item.effectContract.outcome === 'supported'));
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.xcsEvidence; }],
    ['changed suite identity', f => { f.evidence.xcsEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.xcsEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.xcsEvidence.counts = { supported: 2 }; }],
    ['a stale observation digest', f => { f.evidence.xcsEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.xcsEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.xcsEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.xcsEvidence.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.xcsContractsVersion = 2; }],
    ['a missing adapter declaration', f => { delete f.context.xcsAdapterId; }],
    ['a changed adapter bundle declaration', f => { f.context.xcsBundleSha256 = '0'.repeat(64); }],
    ...predecessors.flatMap(name => [
        [`a missing ${name} predecessor`, f => { delete f.evidence[`${name}Evidence`]; }],
        [`a changed ${name} predecessor bundle`, f => { f.context[`${name}BundleSha256`] = '0'.repeat(64); }],
    ]),
]) test('the xterm color-stack receipt refuses ' + label, () => fixture(f => { mutate(f); f.write('context.json', f.context); assert.throws(own(f), refusal); }));

test('the xterm color-stack receipt refuses an adapter that is not the retained layered derivation', () => fixture(f => {
    writeFileSync(join(f.output, 'collector-xcs.mjs'), readFileSync(join(f.output, 'collector-xcs.mjs'), 'utf8') + '\n// edited\n');
    rebind(f, 'adapterBundleSha256', 'collector-xcs.mjs');
    f.context.xcsBundleSha256 = f.evidence.xcsEvidence.adapterBundleSha256; f.write('context.json', f.context);
    assert.throws(own(f), /not the retained layered derivation/);
}));

test('the xterm color-stack receipt refuses a changed run log and a changed report even when rehashed', () => fixture(f => {
    const run = JSON.parse(readFileSync(join(f.output, 'xcs-run.json')));
    run.cases[0].pinned.terminal.pass = true;
    writeFileSync(join(f.output, 'xcs-run.json'), JSON.stringify(run) + '\n'); rebind(f, 'runSha256', 'xcs-run.json');
    assert.throws(own(f), refusal, 'run log');
}));

test('the xterm color-stack receipt refuses a report that no longer equals its rederivation', () => fixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'xcs-observations.json')));
    report.cases[1].effectContract.outcome = 'unsupported';
    writeFileSync(join(f.output, 'xcs-observations.json'), JSON.stringify(report, null, 2) + '\n'); rebind(f, 'sha256', 'xcs-observations.json');
    assert.throws(f.finish, /observations do not match retained evidence/);
}));

test('the xterm color-stack receipt refuses edited, truncated, emptied and missing journals', () => fixture(f => {
    const journal = join(f.output, 'xcs-snapshots.jsonl'), lines = readFileSync(journal, 'utf8').trimEnd().split('\n');
    const edited = JSON.parse(lines[7]); edited.screen.lines[1] = 'edited-in-the-journal';
    writeFileSync(journal, [...lines.slice(0, 7), JSON.stringify(edited), ...lines.slice(8)].join('\n') + '\n');
    assert.throws(own(f), refusal, 'an edited frame');
    writeFileSync(journal, lines.slice(0, -1).join('\n') + '\n');
    assert.throws(own(f), refusal, 'a truncated journal');
    writeFileSync(journal, '');
    assert.throws(own(f), /journal is empty/, 'an emptied journal');
    rmSync(journal);
    assert.throws(own(f), refusal, 'a missing journal');
}));

test('the xterm color-stack receipt refuses retained files that are symlinks', () => fixture(f => {
    rmSync(join(f.output, 'xcs-run.json'));
    writeFileSync(join(f.output, 'xcs-run-target.json'), '{}');
    symlinkSync(join(f.output, 'xcs-run-target.json'), join(f.output, 'xcs-run.json'));
    assert.throws(own(f), /not a regular file/);
}));
