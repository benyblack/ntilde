import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
import { includesXssEvidence, checkXssEvidence, xssFirstPolicy, xssCounts } from './xss-receipt.mjs';
import { xssAdapterId } from './xss-grading-collector.mjs';
import { xssSuiteId } from './xss-contracts.mjs';
import { layerChain } from './osc133-receipt.mjs';

const fixture = action => baseFixture(action, { routed: true, xss: true });
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const rebind = (f, field, file) => { f.evidence.xssEvidence[field] = hash(readFileSync(join(f.output, file))); };
const own = f => () => checkXssEvidence(f.output, f.evidence, f.context.runId, f.context);
const refusal = error => !(error instanceof TypeError);
const predecessors = layerChain.map(([name]) => name);

test('a complete run carries the xterm SGR-stack suffix: pinned-inconclusive counts, identity and the layered derivation rederive without the collector', () => fixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.equal(f.finish().schemaVersion, 63);
    assert.deepEqual(f.evidence.xssEvidence.counts, { inconclusive: 2 });
    assert.equal(f.context.xssAdapterId, xssAdapterId); assert.equal(f.context.xssSuiteId, xssSuiteId);
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => fixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v47' };
    assert.equal(xssFirstPolicy, 57);
    assert.equal(includesXssEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesXssEvidence(empty, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v57' }), true, 'the current policy always carries the suffix');
    assert.equal(includesXssEvidence(empty, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v56' }), false, 'the color-stack policy does not');
    assert.equal(includesXssEvidence(empty, {}, earlier), false);
    assert.equal(includesXssEvidence(empty, {}, { ...earlier, xssSuiteId: 'x' }), true);
    assert.equal(includesXssEvidence(empty, { xssEvidence: {} }, earlier), true);
    assert.equal(includesXssEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

test('the counts are the pinned verdicts of the report, never the effect verdicts', () => fixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'xss-observations.json')));
    assert.deepEqual(xssCounts(report), { inconclusive: 2 });
    assert.ok(report.cases.every(item => item.effectContract.outcome === 'supported'));
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.xssEvidence; }],
    ['changed suite identity', f => { f.evidence.xssEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.xssEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.xssEvidence.counts = { supported: 2 }; }],
    ['a stale observation digest', f => { f.evidence.xssEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.xssEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.xssEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.xssEvidence.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.xssContractsVersion = 2; }],
    ['a missing adapter declaration', f => { delete f.context.xssAdapterId; }],
    ['a changed adapter bundle declaration', f => { f.context.xssBundleSha256 = '0'.repeat(64); }],
    ...predecessors.flatMap(name => [
        [`a missing ${name} predecessor`, f => { delete f.evidence[`${name}Evidence`]; }],
        [`a changed ${name} predecessor bundle`, f => { f.context[`${name}BundleSha256`] = '0'.repeat(64); }],
    ]),
]) test('the xterm SGR-stack receipt refuses ' + label, () => fixture(f => { mutate(f); f.write('context.json', f.context); assert.throws(own(f), refusal); }));

test('the xterm SGR-stack receipt refuses an adapter that is not the retained layered derivation', () => fixture(f => {
    writeFileSync(join(f.output, 'collector-xss.mjs'), readFileSync(join(f.output, 'collector-xss.mjs'), 'utf8') + '\n// edited\n');
    rebind(f, 'adapterBundleSha256', 'collector-xss.mjs');
    f.context.xssBundleSha256 = f.evidence.xssEvidence.adapterBundleSha256; f.write('context.json', f.context);
    assert.throws(own(f), /not the retained layered derivation/);
}));

test('the xterm SGR-stack receipt refuses a changed run log and a changed report even when rehashed', () => fixture(f => {
    const run = JSON.parse(readFileSync(join(f.output, 'xss-run.json')));
    run.cases[0].pinned.terminal.pass = true;
    writeFileSync(join(f.output, 'xss-run.json'), JSON.stringify(run) + '\n'); rebind(f, 'runSha256', 'xss-run.json');
    assert.throws(own(f), refusal, 'run log');
}));

test('the xterm SGR-stack receipt refuses a report that no longer equals its rederivation', () => fixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'xss-observations.json')));
    report.cases[1].effectContract.outcome = 'unsupported';
    writeFileSync(join(f.output, 'xss-observations.json'), JSON.stringify(report, null, 2) + '\n'); rebind(f, 'sha256', 'xss-observations.json');
    assert.throws(f.finish, /observations do not match retained evidence/);
}));

test('the xterm SGR-stack receipt refuses edited, truncated, emptied and missing journals', () => fixture(f => {
    const journal = join(f.output, 'xss-snapshots.jsonl'), lines = readFileSync(journal, 'utf8').trimEnd().split('\n');
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

test('the xterm SGR-stack receipt refuses retained files that are symlinks', () => fixture(f => {
    rmSync(join(f.output, 'xss-run.json'));
    writeFileSync(join(f.output, 'xss-run-target.json'), '{}');
    symlinkSync(join(f.output, 'xss-run-target.json'), join(f.output, 'xss-run.json'));
    assert.throws(own(f), /not a regular file/);
}));
