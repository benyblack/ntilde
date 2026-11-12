import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { privateClipboardReceiptFixture as baseFixture } from './fixtures/private-clipboard-receipt.mjs';
const privateClipboardReceiptFixture = action => baseFixture(action, { routed: true, xtgettcap: true });
import { includesXtgettcapEvidence, checkXtgettcapEvidence, xtgettcapFirstPolicy } from './xtgettcap-receipt.mjs';
import { xtgettcapAdapterId } from './xtgettcap-grading-collector.mjs';
import { xtgettcapSuiteId } from './xtgettcap-contracts.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Re-bind a changed retained file so only the semantic change is under test, not the stale digest. */
const rebind = (f, field, file) => { f.evidence.xtgettcapEvidence[field] = hash(readFileSync(join(f.output, file))); };
/** This layer's own receipt check over the fixture's current files and declarations; refusals must be deliberate, never a TypeError. */
const own = f => () => checkXtgettcapEvidence(f.output, f.evidence, f.context.runId, f.context);
const refusal = error => !(error instanceof TypeError);
const predecessors = ['sgrColors', 'sgrRgb', 'sgrAttrs', 'sgrPalette', 'sgrTruecolor', 'sgrUnderline', 'sgrUnderlineColor', 'sgrReset', 'sgrClosure', 'eraseLine',
    'charEdit', 'eraseDisplay', 'lineEdit', 'charsets', 'scrollback', 'savedReset', 'eraseAttrsRegion', 'attrRect', 'cursorQual', 'eraseCharsHistory',
    'selectiveErase', 'rectEdit', 'colEdit', 'rectChecksum', 'lrMargins', 'titleStack'];

test('a complete run carries the XTGETTCAP suffix: pinned-preserved counts, adapter identity and the layered derivation rederive without the collector', () => privateClipboardReceiptFixture(f => {
    assert.doesNotThrow(own(f), 'the layer check accepts the untouched fixture, so every refusal below is caused by its mutation');
    assert.equal(f.finish().schemaVersion, 53);
    assert.deepEqual(f.evidence.xtgettcapEvidence.counts, { inconclusive: 1 }, 'the pinned verdict is the published conclusion');
    assert.equal(f.context.xtgettcapAdapterId, xtgettcapAdapterId);
    assert.equal(f.context.xtgettcapSuiteId, xtgettcapSuiteId);
    for (const name of predecessors) assert.ok(f.evidence[`${name}Evidence`] !== undefined, name);
}));

test('the suffix is required whenever the policy, a declaration or an artifact names it', () => privateClipboardReceiptFixture(f => {
    const empty = join(f.output, 'no-such-directory'), earlier = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v47' };
    assert.equal(xtgettcapFirstPolicy, 48);
    assert.equal(includesXtgettcapEvidence(f.output, f.evidence, f.context), true);
    assert.equal(includesXtgettcapEvidence(empty, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v48' }), true, 'the current policy always carries the suffix');
    assert.equal(includesXtgettcapEvidence(empty, {}, earlier), false, 'an earlier policy without any artifact or declaration does not');
    assert.equal(includesXtgettcapEvidence(empty, {}, { ...earlier, xtgettcapSuiteId: 'x' }), true);
    assert.equal(includesXtgettcapEvidence(empty, { xtgettcapEvidence: {} }, earlier), true);
    assert.equal(includesXtgettcapEvidence(f.output, {}, earlier), true, 'a retained artifact alone requires it');
}));

for (const [label, mutate] of [
    ['a missing suite binding', f => { delete f.evidence.xtgettcapEvidence; }],
    ['changed suite identity', f => { f.evidence.xtgettcapEvidence.suiteId = 'unrelated'; }],
    ['changed run identity', f => { f.evidence.xtgettcapEvidence.runId = 'b'.repeat(32); }],
    ['changed counts', f => { f.evidence.xtgettcapEvidence.counts = { supported: 1 }; }],
    ['a stale observation digest', f => { f.evidence.xtgettcapEvidence.sha256 = '0'.repeat(64); }],
    ['a stale run-log digest', f => { f.evidence.xtgettcapEvidence.runSha256 = '0'.repeat(64); }],
    ['a stale adapter digest', f => { f.evidence.xtgettcapEvidence.adapterBundleSha256 = '0'.repeat(64); }],
    ['a changed contract version', f => { f.evidence.xtgettcapEvidence.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.xtgettcapContractsVersion = 2; }],
    ['a missing adapter declaration', f => { delete f.context.xtgettcapAdapterId; }],
    ['a missing suite declaration', f => { delete f.context.xtgettcapSuiteId; }],
    ['a changed adapter declaration', f => { f.context.xtgettcapAdapterId = 'other'; }],
    ['a changed adapter bundle declaration', f => { f.context.xtgettcapBundleSha256 = '0'.repeat(64); }],
    ...predecessors.flatMap(name => [
        [`a missing ${name} predecessor`, f => { delete f.evidence[`${name}Evidence`]; }],
        [`a missing ${name} predecessor declaration`, f => { delete f.context[`${name}AdapterId`]; }],
        [`a changed ${name} predecessor bundle`, f => { f.context[`${name}BundleSha256`] = '0'.repeat(64); }],
    ]),
]) test('the XTGETTCAP receipt refuses ' + label, () => privateClipboardReceiptFixture(f => { mutate(f); f.write('context.json', f.context); assert.throws(own(f), refusal); }));

test('the XTGETTCAP receipt refuses an adapter that is not the retained layered derivation', () => privateClipboardReceiptFixture(f => {
    writeFileSync(join(f.output, 'collector-xgtc.mjs'), readFileSync(join(f.output, 'collector-xgtc.mjs'), 'utf8') + '\n// edited\n');
    rebind(f, 'adapterBundleSha256', 'collector-xgtc.mjs');
    f.context.xtgettcapBundleSha256 = f.evidence.xtgettcapEvidence.adapterBundleSha256; f.write('context.json', f.context);
    assert.throws(own(f), /not the retained layered derivation/);
}));

test('the XTGETTCAP receipt refuses a changed run log even when the binding is rehashed to match', () => privateClipboardReceiptFixture(f => {
    const run = JSON.parse(readFileSync(join(f.output, 'xgtc-run.json')));
    run.cases[0].pinned.termless.pass = true;
    writeFileSync(join(f.output, 'xgtc-run.json'), JSON.stringify(run, null, 2) + '\n'); rebind(f, 'runSha256', 'xgtc-run.json');
    assert.throws(own(f), refusal);
}));

test('the XTGETTCAP receipt refuses a changed report that no longer equals its rederivation', () => privateClipboardReceiptFixture(f => {
    const report = JSON.parse(readFileSync(join(f.output, 'xgtc-observations.json')));
    report.cases[0].effectContract.outcome = 'unsupported';
    writeFileSync(join(f.output, 'xgtc-observations.json'), JSON.stringify(report, null, 2) + '\n'); rebind(f, 'sha256', 'xgtc-observations.json');
    f.evidence.xtgettcapEvidence.counts = { unsupported: 1 };
    assert.throws(f.finish, /observations do not match retained evidence/);
}));

test('the XTGETTCAP receipt refuses journal frames edited in the retained journal, a truncated journal and a missing retained artifact', () => privateClipboardReceiptFixture(f => {
    const journal = join(f.output, 'xgtc-snapshots.jsonl'), lines = readFileSync(journal, 'utf8').trimEnd().split('\n');
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

test('the XTGETTCAP receipt refuses a retained file that is not a regular file', () => privateClipboardReceiptFixture(f => {
    rmSync(join(f.output, 'xgtc-run.json'));
    writeFileSync(join(f.output, 'xgtc-run-target.json'), '{}');
    symlinkSync(join(f.output, 'xgtc-run-target.json'), join(f.output, 'xgtc-run.json'));
    assert.throws(own(f), /not a regular file/);
}));
