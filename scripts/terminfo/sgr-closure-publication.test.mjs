// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked
// against the committed evidence. The publication is split into reviewable files (summary, report, run log, common
// frames and one file per read frame); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildSgrClosureGradingReport } from './sgr-closure-grading.mjs';
import { sgrClosureAdapterId } from './sgr-closure-grading-collector.mjs';
import { sgrClosureSuiteId, sgrClosureContracts } from './sgr-closure-contracts.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-08-sgr-closure/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const ids = ['sgr.blink', 'sgr.overline', 'sgr.underline.color'];
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const load = () => {
    const summary = read('summary.json'), report = read('report.json'), run = read('run-log.json'), common = read('journal-common.json');
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of sgrClosureContracts) {
        for (let index = 0; index < contract.reads; index++) {
            const journal = read(`journal-${contract.caseId}-read${index}.json`);
            assert.equal(journal.caseId, contract.caseId);
            assert.equal(journal.read, index);
            snapshots.push(journal.frame);
        }
    }
    snapshots.push(common.frames.finish);
    return { summary, run, report: { ...report, snapshots } };
};

test('the published closure evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildSgrClosureGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, sgrClosureSuiteId);
    assert.equal(report.adapter.id, sgrClosureAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-sgr-selective-reset-grading-v1');
    assert.equal(report.snapshots.length, 3 + 4 + 5 + 2);
    assert.deepEqual(report.cases.map(item => item.caseId), ids);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.pinned.observation.outcome]), ids.map(() => ['supported', 'supported']));
    for (const item of report.cases) assert.equal(item.divergence, undefined);
});

test('sgr.blink: the stored blink flag turns on for the pinned fixture and off for the reset control, independent of other attributes', () => {
    const blink = load().report.cases[0].observed;
    assert.deepEqual([blink.baseline.char, blink.baseline.blink, blink.baseline.flags], ['C', false, 768]);
    assert.deepEqual([blink.target.char, blink.target.blink, blink.target.flags & 0x20], ['X', true, 0x20]);
    assert.equal(blink.target.overline, false);
    assert.deepEqual(blink.trailing.map(cell => [cell.char, cell.blink]), [['X', true], ['Y', false]]);
    assert.equal(blink.controls.decoy.blink, false, 'every other attribute set, blink still false');
    assert.equal(blink.controls.decoy.overline, true);
    assert.equal(blink.reverseVideo, false);
});

test('sgr.overline: the explicit overline boolean is true for the fixture, independent of flags, underline style and color', () => {
    const overline = load().report.cases[1].observed;
    assert.deepEqual([overline.target.char, overline.target.overline, overline.target.flags, overline.target.underline], ['X', true, 768, false]);
    assert.deepEqual([overline.baseline.overline, overline.trailing[1].overline], [false, false]);
    assert.equal(overline.controls.decoy.overline, false, 'underline, underline color and every other attribute set, overline still false');
    assert.equal(overline.controls.decoy.underlineArgb & 0xffffff, 0xff0080);
    const [v, u, t] = overline.controls.independence;
    assert.deepEqual([v.overline, v.underline, u.overline, u.underline, t.overline], [true, 'single', true, false, false]);
});

test('sgr.underline.color is graded from its own reads under its own pinned text and keeps the null default representation', () => {
    const { report } = load();
    const color = report.cases[2];
    assert.equal(color.pinned.assertions[0].expected, 'sgr.underline.color: X has explicit rgb(255,0,128) underline distinct from the reset/default B cell');
    const [a, b, x] = color.observed.fixture;
    assert.deepEqual([a.underlineSource, a.underlineColor, b.underlineSource, b.underlineColor], ['default', null, 'default', null]);
    assert.deepEqual([x.underlineSource, x.underlineColorIndex, x.underlineArgb & 0xffffff, x.underlineColor], ['literal', -1, 0xff0080, { r: 255, g: 0, b: 128 }]);
    assert.deepEqual(color.conclusion.layers, { stored: 'supported', resolved: 'supported' });
    assert.match(color.conclusion.scope, /not claimed/);
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 24);
    assert.equal(receipt.sgrClosureEvidence.suiteId, sgrClosureSuiteId);
    assert.equal(receipt.sgrClosureEvidence.runId, summary.context.runId);
    assert.deepEqual(receipt.sgrClosureEvidence.counts, { supported: 3 });
    assert.equal(receipt.sgrClosureEvidence.adapterBundleSha256, summary.context.sgrClosureBundleSha256);
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.sgrClosureEvidence.sha256, observationsHash);
    assert.equal(receipt.sgrClosureEvidence.runSha256, runHash);
    assert.equal(summary.retained.sgrClosureObservationsSha256, observationsHash);
    assert.equal(summary.retained.sgrClosureRunSha256, runHash);
    assert.equal(summary.retained.sgrClosureJournalSha256, journalHash);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v18');
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.ownedRunId, summary.context.runId);
    assert.equal(summary.runId, undefined);
    assert.notEqual(summary.stockCollectorRunId, summary.ownedRunId);
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.equal(unique.size, 31, 'the prior twenty-nine qualifications plus the two merged selective-reset cases');
    for (const suite of Object.values(summary.sameRun)) assert.ok(suite.cases.every(item => item.outcome === 'supported'));
    assert.ok(!ids.some(id => unique.has(id)), 'the three new cases are counted separately');
    // Each predecessor and finding report is bound to the receipt by hash, so an inconclusive report cannot hide behind the summary.
    const keys = { 'ascii-sgr': 'gradingEvidence', colors: 'sgrColorsEvidence', rgb: 'sgrRgbEvidence', attrs: 'sgrAttrsEvidence', palette: 'sgrPaletteEvidence',
        truecolor: 'sgrTruecolorEvidence', underline: 'sgrUnderlineEvidence', underlineColor: 'sgrUnderlineColorEvidence', reset: 'sgrResetEvidence' };
    for (const [suite, key] of Object.entries(keys)) {
        assert.equal(summary.sameRun[suite].observationsSha256, receipt[key].sha256, suite);
        assert.equal(summary.sameRun[suite].receiptSha256, receipt[key].sha256, suite);
        const counts = {};
        for (const item of summary.sameRun[suite].cases) counts[item.outcome] = (counts[item.outcome] ?? 0) + 1;
        assert.deepEqual(counts, receipt[key].counts, `${suite} case outcomes match the receipt counts`);
    }
    assert.equal(summary.truecolorFinding.observationsSha256, receipt.truecolorFindingEvidence.sha256);
    assert.equal(summary.underlineResolved.observationsSha256, receipt.underlineResolvedEvidence.sha256);
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock run keeps its own grades; the pinned terminal paths never verify SGR styling', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of [...ids, 'sgr.underline-color-rgb']) {
        assert.equal(summary.stockCollector.probes[id].outcome, 'inconclusive', id);
        assert.equal(summary.stockCollector.probes[id].note, 'Cursor advance does not verify SGR styling', id);
    }
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of readdirSync(dir)) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('prior publications still rederive beside this batch and carry no closure evidence', () => {
    const reset = read('../2026-11-08-sgr-selective-reset/summary.json');
    assert.equal(reset.receipt.schemaVersion, 23);
    assert.equal(reset.receipt.sgrClosureEvidence, undefined);
    const { attempts } = load().summary;
    assert.equal(attempts.length, 2, 'both superseded measurements are preserved');
    for (const attempt of attempts) {
        assert.equal(attempt.receiptCompleted, true);
        assert.notEqual(attempt.runId, load().summary.context.runId);
    }
});
