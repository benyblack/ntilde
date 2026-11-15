import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
import { includesKclEvidence, checkKclEvidence, kclFirstPolicy, kclCounts } from './kcl-receipt.mjs';
import { kclAdapterId } from './kcl-grading-collector.mjs';
import { kclSuiteId } from './kcl-contracts.mjs';
import { layerChain } from './osc133-receipt.mjs';

const fixture = action => baseFixture(action, { routed: true, kcl: true });
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const rebind = (f, field, file) => { f.evidence.kclEvidence[field] = hash(readFileSync(join(f.output, file))); };
const own = f => () => checkKclEvidence(f.output, f.evidence, f.context.runId, f.context);
const refusal = error => !(error instanceof TypeError);
const predecessors = layerChain.map(([name]) => name);

test('a complete run carries the kitty color suffix: pinned-supported counts, identity and the layered derivation rederive without the collector', () => fixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.doesNotThrow(() => f.finish());
    assert.deepEqual(f.evidence.kclEvidence.counts, { supported: 1 });
    assert.equal(f.context.kclAdapterId, kclAdapterId); assert.equal(f.context.kclSuiteId, kclSuiteId);
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => fixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v47' };
    const first = `readonly-owned-source-graph-v${kclFirstPolicy}`, before = `readonly-owned-source-graph-v${kclFirstPolicy - 1}`;
    assert.equal(includesKclEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesKclEvidence(empty, {}, { sourceSnapshotPolicy: first }), true, 'the first kitty color policy always carries the suffix');
    assert.equal(includesKclEvidence(empty, {}, { sourceSnapshotPolicy: before }), false, 'the previous policy does not');
    assert.equal(includesKclEvidence(empty, {}, earlier), false);
    assert.equal(includesKclEvidence(empty, {}, { ...earlier, kclSuiteId: 'x' }), true);
    assert.equal(includesKclEvidence(empty, { kclEvidence: {} }, earlier), true);
    assert.equal(includesKclEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

test('the counts are the pinned verdicts of the report, never the effect verdicts', () => fixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'kcl-observations.json')));
    assert.deepEqual(kclCounts(report), { supported: 1 });
    report.cases[0].conclusion.outcome = 'inconclusive';
    assert.deepEqual(kclCounts(report), { inconclusive: 1 });
    assert.ok(JSON.parse(readFileSync(join(f.output, 'kcl-observations.json'))).cases.every(item => item.effectContract.outcome === 'supported'));
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.kclEvidence; }],
    ['changed suite identity', f => { f.evidence.kclEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.kclEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.kclEvidence.counts = { inconclusive: 1 }; }],
    ['a stale observation digest', f => { f.evidence.kclEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.kclEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.kclEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.kclEvidence.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.kclContractsVersion = 2; }],
    ['a missing adapter declaration', f => { delete f.context.kclAdapterId; }],
    ['a changed adapter bundle declaration', f => { f.context.kclBundleSha256 = '0'.repeat(64); }],
    ...predecessors.flatMap(name => [
        [`a missing ${name} predecessor`, f => { delete f.evidence[`${name}Evidence`]; }],
        [`a changed ${name} predecessor bundle`, f => { f.context[`${name}BundleSha256`] = '0'.repeat(64); }],
    ]),
]) test('the kitty color receipt refuses ' + label, () => fixture(f => { mutate(f); f.write('context.json', f.context); assert.throws(own(f), refusal); }));

test('the kitty color receipt refuses an adapter that is not the retained layered derivation', () => fixture(f => {
    writeFileSync(join(f.output, 'collector-kcl.mjs'), readFileSync(join(f.output, 'collector-kcl.mjs'), 'utf8') + '\n// edited\n');
    rebind(f, 'adapterBundleSha256', 'collector-kcl.mjs');
    f.context.kclBundleSha256 = f.evidence.kclEvidence.adapterBundleSha256; f.write('context.json', f.context);
    assert.throws(own(f), /not the retained layered derivation/);
}));

test('the kitty color receipt refuses a changed run log even when rehashed', () => fixture(f => {
    const run = JSON.parse(readFileSync(join(f.output, 'kcl-run.json')));
    run.cases[0].pinned.terminal.pass = false;
    writeFileSync(join(f.output, 'kcl-run.json'), JSON.stringify(run) + '\n'); rebind(f, 'runSha256', 'kcl-run.json');
    assert.throws(own(f), refusal, 'run log');
}));

test('the kitty color receipt refuses a wrong retained reply even when the report and journal are rehashed', () => fixture(f => {
    const journal = join(f.output, 'kcl-snapshots.jsonl'), lines = readFileSync(journal, 'utf8').trimEnd().split('\n');
    const edited = JSON.parse(lines[5]);
    edited.report.replies = ['\x1b]21;foreground=rgb:00/00/00\x1b\\'];
    writeFileSync(journal, [...lines.slice(0, 5), JSON.stringify(edited), ...lines.slice(6)].join('\n') + '\n');
    assert.throws(own(f), refusal, 'a reply edited in the journal alone is not what the worker recorded');
}));

test('the kitty color receipt refuses a report that no longer equals its rederivation', () => fixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'kcl-observations.json')));
    report.cases[0].effectContract.outcome = 'unsupported';
    writeFileSync(join(f.output, 'kcl-observations.json'), JSON.stringify(report, null, 2) + '\n'); rebind(f, 'sha256', 'kcl-observations.json');
    assert.throws(own(f), /observations do not match retained evidence/);
}));

test('the kitty color receipt refuses edited, truncated, emptied and missing journals', () => fixture(f => {
    const journal = join(f.output, 'kcl-snapshots.jsonl'), lines = readFileSync(journal, 'utf8').trimEnd().split('\n');
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

test('the kitty color receipt refuses retained files that are symlinks', () => fixture(f => {
    rmSync(join(f.output, 'kcl-run.json'));
    writeFileSync(join(f.output, 'kcl-run-target.json'), '{}');
    symlinkSync(join(f.output, 'kcl-run-target.json'), join(f.output, 'kcl-run.json'));
    assert.throws(own(f), /not a regular file/);
}));
