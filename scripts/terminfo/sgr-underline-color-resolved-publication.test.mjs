// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked
// against the committed evidence. The publication is split into reviewable files (summary, per-case reports, run log
// and one journal per case); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { buildUnderlineResolvedReport } from './sgr-underline-color-resolved-grading.mjs';
import { underlineResolvedSuiteId, underlineResolvedContracts, underlineResolvedMutation } from './sgr-underline-color-resolved-contracts.mjs';
const dir = new URL('../../docs/qa/terminfo/2026-11-08-underline-color-resolved/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const ids = ['ntilde.underline-color-resolved.indexed', 'ntilde.underline-color-resolved.literal', 'ntilde.underline-color-resolved.default-inheritance'];
const load = () => {
    const summary = read('summary.json'), common = read('report-common.json'), run = read('run-log.json'), journals = read('journal-common.json');
    const snapshots = [journals.frames.start, journals.frames.bind];
    for (const id of ids) {
        const journal = read(`journal-${id}.json`);
        assert.equal(journal.caseId, id);
        snapshots.push(...journal.frames);
    }
    snapshots.push(journals.frames.finish);
    return { summary, run, report: { ...common, cases: ids.map(id => read(`report-${id}.json`)), snapshots } };
};
const hex = argb => (argb >>> 0).toString(16);

test('the published underline-resolution evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildUnderlineResolvedReport(report.snapshots, run, summary.context.runId, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(derived), JSON.stringify(report));
    assert.equal(report.suiteId, underlineResolvedSuiteId);
    assert.equal(report.snapshots.length, 3 + 9);
    assert.deepEqual(report.cases.map(item => item.caseId), ids);
    assert.deepEqual(report.cases.map(item => item.conclusion.outcome), ['supported', 'supported', 'supported']);
    assert.equal(underlineResolvedContracts.length, 3);
});

test('publication digests, schema and policy match the committed bytes', () => {
    const { summary } = load();
    assert.equal(summary.receipt.schemaVersion, 22);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v16');
    assert.equal(summary.receipt.underlineResolvedEvidence.suiteId, underlineResolvedSuiteId);
    assert.deepEqual(summary.receipt.underlineResolvedEvidence.counts, { supported: 3 });
    assert.equal(createHash('sha256').update(JSON.stringify(summary.attempts)).digest('hex').length, 64);
    assert.ok(summary.attempts.length >= 1);
    assert.equal(summary.scriptsRevision, summary.appRevision);
});

test('every App-observed underline equals the independent expectation, with the mutation visible in the binding', () => {
    const { report } = load();
    for (const item of report.cases) {
        for (const group of ['before', 'mutated', 'restored']) {
            for (const cell of item.observed[group]) assert.equal(JSON.stringify(cell.appObserved), JSON.stringify(cell.parentExpected));
        }
        assert.deepEqual(item.bindings[1].paletteOverrideIndices, [4, 5]);
        assert.equal(item.bindings[1].entry4Argb, underlineResolvedMutation[4]);
        assert.equal(item.bindings[1].entry5Argb, underlineResolvedMutation[5]);
        assert.deepEqual(item.bindings[2].paletteOverrideIndices, []);
        assert.equal(item.bindings[0].bindingSha256, item.bindings[2].bindingSha256);
        assert.notEqual(item.bindings[0].bindingSha256, item.bindings[1].bindingSha256);
    }
});

test('indexed underlines follow the palette, literals do not, and defaults inherit the cell text channel', () => {
    const { report } = load();
    const [indexed, literal, inherit] = report.cases.map(item => item.observed);
    const at = (group, char) => group.find(cell => cell.char === char).appObserved;
    // Indexed: stored palette 5 / 4; the App's resolved color follows entries 5 / 4 across the OSC 4 mutation and returns.
    assert.deepEqual([at(indexed.before, 'X').source, at(indexed.before, 'X').index, hex(at(indexed.before, 'X').argb)], ['palette', 5, 'ffcd00cd']);
    assert.equal(hex(at(indexed.mutated, 'X').argb), 'ff445566');
    assert.equal(hex(at(indexed.mutated, 'Y').argb), 'ff112233');
    assert.equal(hex(at(indexed.restored, 'X').argb), 'ffcd00cd');
    assert.equal(hex(at(indexed.restored, 'Y').argb), 'ff0000ee');
    // Literal: stored bytes; P equals the default entry 5 color but is a literal and does not follow its mutation.
    assert.deepEqual([at(literal.before, 'P').source, hex(at(literal.mutated, 'P').argb)], ['literal', 'ffcd00cd']);
    assert.equal(hex(at(literal.mutated, 'X').argb), 'ffff0080');
    // Default inheritance: the underline takes the foreground (or the background under inverse) and follows its palette entry.
    assert.deepEqual([at(inherit.before, 'B').inheritsFrom, hex(at(inherit.before, 'B').argb), hex(at(inherit.mutated, 'B').argb)], ['foreground', 'ffcd00cd', 'ff445566']);
    assert.deepEqual([at(inherit.before, 'D').inheritsFrom, hex(at(inherit.before, 'D').argb)], ['background', 'ff000000']);
    assert.deepEqual([at(inherit.before, 'J').inheritsFrom, hex(at(inherit.mutated, 'J').argb)], ['background', 'ff445566']);
    assert.equal(at(inherit.before, 'E').source, 'palette'); // explicit under inverse is untouched by it
    assert.equal(at(inherit.before, 'G').source, 'none');
});

test('predecessor evidence rides in the same run and keeps its counts', () => {
    const { summary } = load();
    const outcomes = layer => summary.sameRun[layer].cases.map(item => item.outcome);
    assert.deepEqual(outcomes('sgrUnderlineColor'), ['supported', 'supported', 'supported']);
    assert.equal(outcomes('sgrUnderline').length, 4);
    assert.equal(outcomes('truecolorFinding').length, 10);
    assert.ok(outcomes('truecolorFinding').every(outcome => outcome === 'supported'));
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    // Some benchmark cases are qualified by more than one layer: count unique ids, all supported.
    const unique = new Set(['sgrUnderlineColor', 'sgrUnderline', 'sgrTruecolor', 'sgrPalette', 'sgrAttrs', 'sgrRgb', 'sgrColors', 'grading']
        .flatMap(layer => summary.sameRun[layer].cases.filter(item => item.outcome === 'supported').map(item => item.caseId)));
    assert.equal(unique.size, 29);
});
