// Frozen real-App publication of the truecolor-literal finding: one run against main's App
// (the parser snaps five values) and one against the fixed App, with the same scripts. Synthetic
// fixtures do not replace these measurements. The publication is split into small reviewable files
// per run (context/receipt, run log, common report, one report and one journal per case).
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildTruecolorFindingReport } from './truecolor-literal-finding-grading.mjs';
import { truecolorFindingContracts, truecolorFindingSuiteId, snappedValues } from './truecolor-literal-finding-contracts.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-08-truecolor-literal-finding/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const ids = truecolorFindingContracts.map(contract => contract.caseId);
const summary = read('summary.json');

const load = label => {
    const common = read(`${label}/journal-common.json`), reportCommon = read(`${label}/report-common.json`);
    const snapshots = [common.frames.start, common.frames.bind];
    for (const id of ids) {
        const journal = read(`${label}/journal-${id}.json`);
        assert.equal(journal.caseId, id);
        assert.equal(journal.frames.length, 3);
        snapshots.push(...journal.frames);
    }
    snapshots.push(common.frames.finish);
    const cases = ids.map(id => read(`${label}/report-${id}.json`));
    return { run: read(`${label}/run-log.json`), contextReceipt: read(`${label}/context-receipt.json`),
        report: { ...reportCommon, cases, snapshots } };
};

for (const label of ['before', 'after']) {
    test(`the published ${label} finding re-derives exactly from its retained journal and run log`, () => {
        const { run, report } = load(label);
        const derived = buildTruecolorFindingReport(report.snapshots, run, summary[label].runId, { contractsVersion: report.contractsVersion });
        assert.deepEqual(report, derived);
        assert.equal(report.suiteId, truecolorFindingSuiteId);
        assert.equal(report.kind, 'ntilde-finding-not-a-terminfo-case');
        assert.equal(report.snapshots.length, 3 + 10 * 3);
        assert.deepEqual(report.cases.map(item => item.caseId), ids);
    });
}

test('before the fix every value was stored as its palette index and followed the palette; after it stays a literal', () => {
    const before = load('before').report, after = load('after').report;
    assert.ok(before.cases.every(item => item.conclusion.outcome === 'unsupported'));
    assert.ok(after.cases.every(item => item.conclusion.outcome === 'supported'));
    for (const item of before.cases) {
        assert.deepEqual(item.conclusion.layers, { stored: 'unsupported', resolved: 'unsupported' });
        for (const finding of Object.values(item.contractResult.findings)) {
            assert.equal(finding.stored, `palette:${item.aliasedIndex}`);
            assert.equal(finding.initialMatchesRequested, false, 'the default theme resolved a different color than requested');
            assert.equal(finding.changedByMutation, true, 'OSC 4 on the aliased entry changed the explicit RGB cell');
        }
    }
    for (const item of after.cases) {
        assert.deepEqual(item.conclusion.layers, { stored: 'supported', resolved: 'supported' });
        for (const finding of Object.values(item.contractResult.findings)) {
            assert.equal(finding.stored, 'literal');
            assert.equal(finding.initialMatchesRequested, true);
            assert.equal(finding.changedByMutation, false);
        }
    }
});

test('the valid controls are the same in both runs: neutral baseline, indexed positive control, exact mutation and restore', () => {
    for (const label of ['before', 'after']) {
        const { report } = load(label);
        for (const item of report.cases) {
            const [baseline, indexed] = [item.observed.before[0], item.observed.before[1]];
            assert.equal(baseline.flags, 768, 'neutral default baseline');
            assert.equal(item.observed.reverseVideo, false);
            const channel = item.channel;
            assert.equal(indexed[`${channel}Source`], 'palette');
            assert.equal(indexed[`${channel}Index`], item.aliasedIndex);
            // The indexed positive control follows the OSC 4 change (magenta) and restores.
            assert.deepEqual(item.observed.mutated[1][channel], { r: 255, g: 0, b: 255 });
            assert.deepEqual(item.observed.restored[1][channel], item.observed.before[1][channel]);
            assert.deepEqual(item.bindings.map(binding => binding.paletteOverrideIndices), [[], [item.aliasedIndex], []]);
            assert.equal(item.bindings[0].bindingSha256, item.bindings[2].bindingSha256, 'the original palette was restored');
            assert.equal(item.bindings[1].entryArgb, 0xffff00ff, 'the aliased entry was set to magenta');
            assert.notEqual(item.bindings[0].entryArgb, 0xffff00ff);
            assert.equal(item.bindings[0].entryArgb, item.bindings[2].entryArgb);
        }
    }
});

test('literal bytes, not just resolved colors, are stored after the fix, in all three forms', () => {
    const { report } = load('after');
    for (const item of report.cases) {
        const { r, g, b } = item.requested;
        for (const position of [2, 3, 4]) {
            for (const phase of ['before', 'mutated', 'restored']) {
                const cell = item.observed[phase][position];
                assert.equal(cell[`${item.channel}Source`], 'literal');
                assert.equal(cell[`${item.channel}Index`], -1);
                assert.equal(cell[item.channel === 'fg' ? 'foregroundArgb' : 'backgroundArgb'] & 0xffffff, (r << 16) | (g << 8) | b);
            }
        }
    }
    assert.equal(snappedValues.length, 5);
});

test('identities: same scripts revision, App revisions differ, both receipts complete at schema 19 with the 22 predecessors', () => {
    const { before, after } = summary;
    assert.equal(before.scriptsRevision, after.scriptsRevision);
    assert.notEqual(before.appRevision, after.appRevision);
    assert.notEqual(before.runId, after.runId);
    for (const run of [before, after]) {
        assert.equal(run.receiptSchemaVersion, 19);
        assert.equal(run.sourceSnapshotPolicy, 'readonly-owned-source-graph-v13');
        assert.equal(run.findingEvidenceBinding.runId, run.runId);
        assert.equal(run.findingEvidenceBinding.suiteId, truecolorFindingSuiteId);
        assert.deepEqual(run.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
        assert.equal(run.stockCollector.suiteComplete, false);
        const unique = new Set(Object.values(run.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
        assert.equal(unique.size, 22, 'the prior twenty-two unique qualifications');
        for (const suite of Object.values(run.sameRun)) assert.ok(suite.cases.every(item => item.outcome === 'supported'));
    }
    assert.deepEqual(before.findingCounts, { unsupported: 10 });
    assert.deepEqual(after.findingCounts, { supported: 10 });
    assert.deepEqual(before.findingEvidenceBinding.counts, before.findingCounts);
});

test('the reconstructed report, run log and journal hash to the receipt and retained digests', () => {
    const hash = value => createHash('sha256').update(value).digest('hex');
    for (const label of ['before', 'after']) {
        const { run, report, contextReceipt } = load(label);
        const binding = contextReceipt.receipt.truecolorFindingEvidence;
        assert.equal(hash(JSON.stringify(report, null, 2) + '\n'), binding.sha256);
        assert.equal(hash(JSON.stringify(report, null, 2) + '\n'), summary[label].retained.findingObservationsSha256);
        assert.equal(hash(JSON.stringify(run) + '\n'), binding.runSha256);
        assert.equal(hash(JSON.stringify(run) + '\n'), summary[label].retained.findingRunSha256);
        assert.equal(hash(report.snapshots.map(frame => JSON.stringify(frame)).join('\n') + '\n'), summary[label].retained.findingJournalSha256);
    }
});

test('the context and receipt files agree with the summary', () => {
    for (const label of ['before', 'after']) {
        const { contextReceipt } = load(label);
        assert.equal(contextReceipt.context.runId, summary[label].runId);
        assert.equal(contextReceipt.receipt.schemaVersion, 19);
        assert.equal(contextReceipt.receipt.fixtureRemoved, true);
        assert.equal(contextReceipt.receipt.truecolorFindingEvidence.sha256, summary[label].findingEvidenceBinding.sha256);
    }
});

test('every publication file stays small enough to review in one piece', () => {
    const files = ['summary.json', ...['before', 'after'].flatMap(label => readdirSync(new URL(`${label}/`, dir)).map(name => `${label}/${name}`))];
    assert.equal(files.length, 1 + 2 * 24);
    for (const name of files) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1200, `${name} has ${lines} lines`);
    }
});

test('failed attempts are preserved and prior publications still rederive beside this finding', () => {
    assert.ok(Array.isArray(summary.attempts));
    const truecolor = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-truecolor/summary.json', import.meta.url)));
    assert.equal(truecolor.receipt.schemaVersion, 18);
    assert.equal(truecolor.receipt.truecolorFindingEvidence, undefined);
    const palette = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-palette/summary.json', import.meta.url)));
    assert.equal(palette.receipt.schemaVersion, 17);
});
