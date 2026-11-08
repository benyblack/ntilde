// Frozen real-App publication. Synthetic fixtures do not replace this
// measurement; every digest below is checked against the committed evidence.
// The publication is split into reviewable files (summary, report, run log and
// one journal per case); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { buildSgrTruecolorGradingReport } from './sgr-truecolor-grading.mjs';
import { sgrTruecolorAdapterId } from './sgr-truecolor-grading-collector.mjs';
import { sgrTruecolorSuiteId, sgrTruecolorContracts } from './sgr-truecolor-contracts.mjs';
const dir = new URL('../../docs/qa/terminfo/2026-11-08-sgr-truecolor/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const ids = ['sgr.fg.truecolor', 'sgr.bg.truecolor'];
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

test('the published SGR-truecolor evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildSgrTruecolorGradingReport(report.snapshots, run, summary.context.runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, sgrTruecolorSuiteId);
    assert.equal(report.adapter.id, sgrTruecolorAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-sgr-palette-grading-v1');
    assert.equal(report.snapshots.length, 3 + 2 * 2);
    assert.deepEqual(report.cases.map(item => item.caseId), ids);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.pinned.observation.outcome]), ids.map(() => ['supported', 'supported']));
    assert.equal(sgrTruecolorContracts.length, 2);
});

test('every conclusion is bound to its exact stored and resolved observation', () => {
    const { report } = load();
    const by = id => report.cases.find(item => item.caseId === id);
    const stored = (id, section, field) => by(id).observed[section].map(cell => [cell.char, cell[field]]);
    assert.deepEqual(stored('sgr.fg.truecolor', 'fixture', 'fgSource'), [['C', 'default'], ['A', 'palette'], ['B', 'palette'], ['X', 'literal'], ['Y', 'literal']]);
    assert.deepEqual(stored('sgr.bg.truecolor', 'fixture', 'bgIndex'), [['C', -1], ['A', 1], ['B', 4], ['X', -1], ['Y', -1]]);
    const rgb = (id, section, position, field) => by(id).observed[section][position][field];
    assert.deepEqual(rgb('sgr.fg.truecolor', 'fixture', 3, 'fg'), { r: 255, g: 128, b: 0 });
    assert.deepEqual(rgb('sgr.fg.truecolor', 'fixture', 4, 'fg'), { r: 17, g: 97, b: 201 });
    assert.deepEqual(rgb('sgr.bg.truecolor', 'fixture', 3, 'bg'), { r: 0, g: 255, b: 128 });
    assert.equal(rgb('sgr.fg.truecolor', 'fixture', 0, 'fg'), null);
    assert.equal(by('sgr.fg.truecolor').observed.fixture[3].foregroundArgb & 0xffffff, 0xff8000);
    assert.equal(by('sgr.bg.truecolor').observed.fixture[3].backgroundArgb & 0xffffff, 0x00ff80);
    // Literal and palette storage resolve alike (95,135,175) yet stay distinct: P literal, Q palette 67.
    const [P, Q, L, H] = by('sgr.fg.truecolor').observed.control;
    assert.deepEqual([P.fgSource, Q.fgSource, Q.fgIndex, L.fgSource, H.fgSource], ['literal', 'palette', 67, 'literal', 'literal']);
    assert.deepEqual(P.fg, Q.fg);
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
});

test('one palette and theme binding covers the whole journal and overline stayed clear on every journaled cell', () => {
    const { report } = load();
    const digests = new Set(report.cases.map(item => item.colorBinding.bindingSha256));
    assert.equal(digests.size, 1);
    for (const frame of report.snapshots.filter(item => item.kind === 'sgtc-read')) {
        assert.ok(frame.screen.cellDetails.every(run => run.overline === false));
    }
});

test('the receipt binds the run, adapters and counts, and every predecessor reconfirms in the same run', () => {
    const { summary } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 18);
    assert.equal(receipt.sgrTruecolorEvidence.suiteId, sgrTruecolorSuiteId);
    assert.equal(receipt.sgrTruecolorEvidence.runId, summary.context.runId);
    assert.equal(receipt.sgrTruecolorEvidence.contractsVersion, 1);
    assert.equal(receipt.sgrTruecolorEvidence.adapterBundleSha256, summary.context.sgrTruecolorBundleSha256);
    assert.deepEqual(receipt.sgrTruecolorEvidence.counts, { supported: 2 });
    assert.deepEqual(receipt.sgrPaletteEvidence.counts, { supported: 6 });
    assert.deepEqual(receipt.sgrAttrsEvidence.counts, { supported: 5 });
    assert.deepEqual(receipt.sgrRgbEvidence.counts, { supported: 2 });
    assert.deepEqual(receipt.sgrColorsEvidence.counts, { supported: 5 });
    assert.deepEqual(receipt.gradingEvidence.counts, { supported: 4 });
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v12');
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.equal(unique.size, 20, 'the prior twenty unique qualifications');
    for (const suite of Object.values(summary.sameRun)) assert.ok(suite.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock run keeps its own grades and both cases stay inconclusive there', () => {
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
    assert.equal(summary.attempts[0].receiptCompleted, false);
    assert.ok(summary.attempts.some(attempt => attempt.outcome === 'complete, superseded' && attempt.receiptCompleted === true));
    const palette = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-palette/summary.json', import.meta.url)));
    assert.equal(palette.receipt.schemaVersion, 17);
    assert.equal(palette.receipt.sgrTruecolorEvidence, undefined);
    const attrs = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-attributes-qualification.json', import.meta.url)));
    assert.equal(attrs.receipt.schemaVersion, 16);
    assert.equal(attrs.receipt.sgrTruecolorEvidence, undefined);
});
