// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is
// checked against the committed evidence. The publication is split into reviewable files (summary, report index,
// one report per case, run log, common frames and one file per read frame); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildLineEditGradingReport } from './line-edit-grading.mjs';
import { lineEditAdapterId } from './line-edit-grading-collector.mjs';
import { lineEditSuiteId, lineEditContracts } from './line-edit-contracts.mjs';
import { eraseLineContracts } from './erase-line-contracts.mjs';
import { asciiSgrContracts } from './ascii-sgr-contracts.mjs';
import { sgrColorsContracts } from './sgr-colors-contracts.mjs';
import { sgrRgbContracts } from './sgr-rgb-contracts.mjs';
import { sgrAttrsContracts } from './sgr-attrs-contracts.mjs';
import { sgrPaletteContracts } from './sgr-palette-contracts.mjs';
import { sgrTruecolorContracts } from './sgr-truecolor-contracts.mjs';
import { sgrUnderlineContracts } from './sgr-underline-contracts.mjs';
import { sgrUnderlineColorContracts } from './sgr-underline-color-contracts.mjs';
import { sgrResetContracts } from './sgr-reset-contracts.mjs';
import { sgrClosureContracts } from './sgr-closure-contracts.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-09-line-editing/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const IL = 'editing.insert-lines', DL = 'editing.delete-lines';
const ids = [IL, DL];
const load = () => {
    const summary = read('summary.json'), index = read('report.json'), run = read('run-log.json'), common = read('journal-common.json');
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of lineEditContracts) {
        for (let at = 0; at < contract.roles.length; at++) {
            const journal = read(`journal-${contract.caseId}-read${at}.json`);
            assert.equal(journal.caseId, contract.caseId);
            assert.equal(journal.read, at);
            snapshots.push(journal.frame);
        }
    }
    snapshots.push(common.frames.finish);
    const { caseFiles, ...rest } = index;
    const cases = caseFiles.map(name => read(name));
    return { summary, run, report: { ...rest, cases, snapshots } };
};
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

test('the published line-editing evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildLineEditGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, lineEditSuiteId);
    assert.equal(report.adapter.id, lineEditAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-erase-line-grading-v1');
    assert.equal(report.snapshots.length, 3 + 2 * 20);
    assert.deepEqual(report.cases.map(item => item.caseId), ids);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.pinned.observation.outcome]), ids.map(() => ['supported', 'supported']));
    for (const item of report.cases) assert.equal(item.divergence, undefined);
});

test('IL and DL moved exactly the contracted lines on a 40x140 screen, with the cursor at row 2 column 1', () => {
    const [il, dl] = load().report.cases.map(item => item.observed);
    for (const observed of [il, dl]) {
        assert.deepEqual(observed.geometry, { rows: 40, cols: 140 });
        assert.equal(observed.reverseVideo, false);
        assert.equal(observed.initial.ink, 0);
        assert.deepEqual(observed.setup.cursor, [1, 0]);
        assert.deepEqual(observed.edited.cursor, [1, 0]);
        assert.deepEqual(observed.edited.margins, [0, 39]);
        assert.equal(observed.edited.origin, false);
        assert.equal(observed.edited.insert, false);
        assert.deepEqual(observed.before.text, ['AAAAA_____', 'BBBBB_____', 'CCCCC_____', 'DDDDD_____', '__________', '__________']);
    }
    assert.deepEqual(il.edited.text, ['AAAAA_____', '__________', 'BBBBB_____', 'CCCCC_____', 'DDDDD_____', '__________']);
    assert.deepEqual(dl.edited.text, ['AAAAA_____', 'CCCCC_____', 'DDDDD_____', '__________', '__________', '__________']);
    assert.deepEqual([il.pinnedSequence, dl.pinnedSequence], ['\x1b[1L', '\x1b[1M']);
});

test('count spellings stay distinct, observed controls and all match the model', () => {
    const [il, dl] = load().report.cases.map(item => item.observed.countControls);
    for (const controls of [il, dl]) {
        for (const [name, control] of Object.entries(controls).filter(([key]) => key !== 'policy')) assert.equal(control.matchesModel, true, name);
        assert.equal(controls.omitted.sameAsPinned, true);
        assert.equal(controls.explicit0.sameAsPinned, true);
        assert.equal(controls.explicit2.sameAsPinned, false);
    }
    assert.equal(il.explicit0.sequence, '\x1b[0L');
    assert.equal(dl.oversized.sequence, '\x1b[999M');
    assert.deepEqual(il.explicit2.text.slice(0, 6), ['AAAAA_____', '__________', '__________', 'BBBBB_____', 'CCCCC_____', 'DDDDD_____']);
    assert.deepEqual(dl.explicit2.text.slice(0, 2), ['AAAAA_____', 'DDDDD_____']);
});

test('the full-height boundary, the scroll region and origin mode confine the edit; margins are read from the stored state', () => {
    const [il, dl] = load().report.cases.map(item => item.observed);
    assert.deepEqual(il.bottomBoundary.after.text, ['TOP-ROW---', '__________', '__________', 'NEXT-LAST-']);
    assert.deepEqual(dl.bottomBoundary.after.text, ['TOP-ROW---', '__________', 'LAST-ROW--', '__________']);
    for (const observed of [il, dl]) {
        assert.deepEqual(observed.scrollRegion.margins, [2, 4]);
        assert.deepEqual(observed.scrollRegion.before.margins, [2, 4]);
        assert.equal(observed.originMode.before.origin, true);
        assert.deepEqual(observed.scrollRegion.cursorAbove.text, observed.scrollRegion.before.text);
        assert.deepEqual(observed.scrollRegion.cursorBelow.text, observed.scrollRegion.before.text);
        assert.deepEqual(observed.originMode.after.text, observed.scrollRegion.after.text);
        assert.deepEqual(observed.scrollRegion.oversizedCount.text.slice(0, 7), ['AAAAAAAAAA', 'BBBBBBBBBB', 'CCCCCCCCCC', '__________', '__________', 'FFFFFFFFFF', 'GGGGGGGGGG']);
    }
    assert.deepEqual(il.scrollRegion.after.text.slice(0, 7), ['AAAAAAAAAA', 'BBBBBBBBBB', 'CCCCCCCCCC', '__________', 'DDDDDDDDDD', 'FFFFFFFFFF', 'GGGGGGGGGG']);
    assert.deepEqual(dl.scrollRegion.after.text.slice(0, 7), ['AAAAAAAAAA', 'BBBBBBBBBB', 'CCCCCCCCCC', 'EEEEEEEEEE', '__________', 'FFFFFFFFFF', 'GGGGGGGGGG']);
});

test('moved cells keep their stored tuple; background fill and cursor column are observed, not required; no scrollback accumulates', () => {
    for (const item of load().report.cases) {
        const { attributes, cursorColumn, history } = item.observed;
        assert.equal(attributes.movedKeepTuple, true);
        assert.equal(attributes.blankLinesBackgroundOnly, true);
        assert.match(attributes.policy, /observed, not required/);
        assert.equal(attributes.backgroundFill, 'new blank lines keep the default background');
        assert.equal(attributes.backgroundFillDetail.pen.bgIndex, 4);
        assert.match(cursorColumn.policy, /observed, not graded/);
        assert.deepEqual([cursorColumn.beforeColumn, cursorColumn.afterColumn], [10, 10]);
        assert.deepEqual(history.scrollbackLines, [0]);
        assert.equal(history.historyLines, 0);
    }
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 26);
    assert.equal(receipt.lineEditEvidence.suiteId, lineEditSuiteId);
    assert.equal(receipt.lineEditEvidence.runId, summary.context.runId);
    assert.deepEqual(receipt.lineEditEvidence.counts, { supported: 2 });
    assert.equal(receipt.lineEditEvidence.adapterBundleSha256, summary.context.lineEditBundleSha256);
    const observationsHash = hash(JSON.stringify({ ...report, cases: report.cases, snapshots: report.snapshots }, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.lineEditEvidence.sha256, observationsHash);
    assert.equal(receipt.lineEditEvidence.runSha256, runHash);
    assert.equal(summary.retained.lineEditObservationsSha256, observationsHash);
    assert.equal(summary.retained.lineEditRunSha256, runHash);
    assert.equal(summary.retained.lineEditJournalSha256, journalHash);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v20');
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.ownedRunId, summary.context.runId);
    assert.notEqual(summary.stockCollectorRunId, summary.ownedRunId);
    // The predecessor inventory is derived from the merged contracts, not assumed.
    const expected = new Set([...asciiSgrContracts, ...sgrColorsContracts, ...sgrRgbContracts, ...sgrAttrsContracts, ...sgrPaletteContracts,
        ...sgrTruecolorContracts, ...sgrUnderlineContracts, ...sgrUnderlineColorContracts, ...sgrResetContracts, ...sgrClosureContracts,
        ...eraseLineContracts].map(contract => contract.caseId));
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...expected].sort());
    for (const suite of Object.values(summary.sameRun)) assert.ok(suite.cases.every(item => item.outcome === 'supported'));
    assert.ok(!ids.some(id => unique.has(id)), 'the two new cases are counted separately');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock run keeps its own grades: both pinned cases are inconclusive there', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of ids) {
        assert.equal(summary.stockCollector.probes[id].outcome, 'inconclusive');
        assert.equal(summary.stockCollector.probes[id].evidence, 'query');
    }
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of readdirSync(dir)) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('prior publications still rederive beside this batch and failed attempts are preserved', () => {
    const erase = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-erase-line/summary.json', import.meta.url)));
    assert.equal(erase.receipt.schemaVersion, 25);
    assert.equal(erase.receipt.lineEditEvidence, undefined);
    const { attempts, context } = load().summary;
    assert.ok(attempts.length >= 1);
    for (const attempt of attempts) assert.notEqual(attempt.runId, context.runId);
    assert.equal(attempts[0].receiptCompleted, false);
});
