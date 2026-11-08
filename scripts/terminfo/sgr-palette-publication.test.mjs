// Frozen real-App publication. Synthetic fixtures do not replace this
// measurement; every digest below is checked against the committed evidence.
// The publication is split into reviewable files (summary, report, run log and
// one journal per case); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { buildSgrPaletteGradingReport } from './sgr-palette-grading.mjs';
import { sgrPaletteAdapterId } from './sgr-palette-grading-collector.mjs';
import { sgrPaletteSuiteId, sgrPaletteContracts } from './sgr-palette-contracts.mjs';
const dir = new URL('../../docs/qa/terminfo/2026-11-08-sgr-palette/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const ids = ['sgr.fg.bright', 'sgr.bg.bright', 'sgr.fg.256', 'sgr.bg.256', 'sgr.fg.default', 'sgr.bg.default'];
const load = () => {
    const summary = read('summary.json'), report = read('report.json'), run = read('run-log.json'), common = read('journal-common.json');
    const snapshots = [common.frames.start, common.frames.bind];
    for (const id of ids) {
        const journal = read(`journal-${id}.json`);
        assert.equal(journal.caseId, id);
        snapshots.push(...journal.frames);
    }
    snapshots.push(common.frames.finish);
    return { summary, run, report: { ...report, snapshots } };
};

test('the published SGR-palette evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildSgrPaletteGradingReport(report.snapshots, run, summary.context.runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, sgrPaletteSuiteId);
    assert.equal(report.adapter.id, sgrPaletteAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-sgr-attributes-grading-v1');
    assert.equal(report.snapshots.length, 3 + 6 * 2);
    assert.deepEqual(report.cases.map(item => item.caseId), ids);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.pinned.observation.outcome]), ids.map(() => ['supported', 'supported']));
    assert.equal(sgrPaletteContracts.length, 6);
});

test('every conclusion is bound to its exact stored and resolved observation', () => {
    const { report } = load();
    const by = id => report.cases.find(item => item.caseId === id);
    const stored = (id, section, field) => by(id).observed[section].map(cell => [cell.char, cell[field]]);
    assert.deepEqual(stored('sgr.fg.bright', 'fixture', 'fgIndex'), [['C', -1], ['X', 9], ['Y', 12]]);
    assert.deepEqual(stored('sgr.bg.bright', 'fixture', 'bgIndex'), [['C', -1], ['X', 9], ['Y', 12]]);
    assert.deepEqual(stored('sgr.fg.256', 'fixture', 'fgSource'), [['C', 'default'], ['A', 'literal'], ['B', 'literal'], ['X', 'palette'], ['Y', 'palette']]);
    assert.deepEqual(stored('sgr.bg.256', 'fixture', 'bgIndex'), [['C', -1], ['A', -1], ['B', -1], ['X', 67], ['Y', 173]]);
    assert.deepEqual(stored('sgr.fg.default', 'fixture', 'fgSource'), [['C', 'default'], ['X', 'palette'], ['R', 'default']]);
    assert.deepEqual(stored('sgr.bg.default', 'fixture', 'bgSource'), [['C', 'default'], ['X', 'palette'], ['R', 'default']]);
    const rgb = (id, section, position, field) => by(id).observed[section][position][field];
    assert.deepEqual(rgb('sgr.fg.256', 'fixture', 3, 'fg'), rgb('sgr.fg.256', 'fixture', 1, 'fg'), 'index 67 resolves to the literal cube reference');
    assert.deepEqual(rgb('sgr.fg.256', 'fixture', 4, 'fg'), { r: 215, g: 135, b: 95 });
    assert.equal(rgb('sgr.fg.default', 'fixture', 0, 'fg'), null);
    assert.equal(rgb('sgr.fg.default', 'fixture', 2, 'fg'), null);
    for (const item of report.cases) {
        assert.equal(item.observed.fixture[0].flags, 768, 'neutral default baseline');
        assert.equal(item.observed.reverseVideo, false);
        assert.equal(item.colorBinding.reverseVideo, false);
        assert.deepEqual(item.colorBinding.paletteOverrideIndices, []);
        assert.equal(item.divergence, undefined);
        assert.deepEqual(item.conclusion.layers, { stored: 'supported', resolved: 'supported' });
        assert.match(item.conclusion.scope, /rendered appearance is not claimed/);
        assert.equal(item.conclusion.evidence, 'owned-stored-and-resolved-cells');
        assert.equal(item.pinned.assertions[0].kind, 'positive');
    }
    assert.equal(by('sgr.fg.bright').observed.control[0].bold, true, 'bold stays a separate attribute');
});

test('one palette and theme binding covers the whole journal and overline stayed clear on every journaled cell', () => {
    const { report } = load();
    const digests = new Set(report.cases.map(item => item.colorBinding.bindingSha256));
    assert.equal(digests.size, 1);
    for (const frame of report.snapshots.filter(item => item.kind === 'sgpal-read')) {
        assert.ok(frame.screen.cellDetails.every(run => run.overline === false));
    }
});

test('the receipt binds the run, adapters and counts, and every predecessor reconfirms in the same run', () => {
    const { summary } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 17);
    assert.equal(receipt.sgrPaletteEvidence.suiteId, sgrPaletteSuiteId);
    assert.equal(receipt.sgrPaletteEvidence.runId, summary.context.runId);
    assert.equal(receipt.sgrPaletteEvidence.contractsVersion, 1);
    assert.equal(receipt.sgrPaletteEvidence.adapterBundleSha256, summary.context.sgrPaletteBundleSha256);
    assert.deepEqual(receipt.sgrPaletteEvidence.counts, { supported: 6 });
    assert.deepEqual(receipt.sgrAttrsEvidence.counts, { supported: 5 });
    assert.deepEqual(receipt.sgrRgbEvidence.counts, { supported: 2 });
    assert.deepEqual(receipt.sgrColorsEvidence.counts, { supported: 5 });
    assert.deepEqual(receipt.gradingEvidence.counts, { supported: 4 });
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v11');
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.equal(unique.size, 14, 'the prior fourteen unique qualifications');
    for (const suite of Object.values(summary.sameRun)) assert.ok(suite.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock run keeps its own grades and all six cases stay inconclusive there', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of ids) {
        assert.equal(summary.stockCollector.probes[id].outcome, 'inconclusive', id);
        assert.equal(summary.stockCollector.probes[id].note, 'Cursor advance does not verify SGR styling', id);
    }
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of ['summary.json', 'report.json', 'run-log.json', 'journal-common.json', ...ids.map(id => `journal-${id}.json`)]) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('failed attempts are preserved and prior publications still rederive beside this batch', () => {
    const { summary } = load();
    assert.ok(summary.attempts.length >= 2);
    assert.ok(summary.attempts.slice(0, 2).every(attempt => attempt.receiptCompleted === false));
    const attrs = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-attributes-qualification.json', import.meta.url)));
    assert.equal(attrs.receipt.schemaVersion, 16);
    assert.equal(attrs.receipt.sgrPaletteEvidence, undefined);
    const rgb = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-07-sgr-rgb-qualification.json', import.meta.url)));
    assert.equal(rgb.receipt.schemaVersion, 15);
    assert.equal(rgb.receipt.sgrPaletteEvidence, undefined);
});
