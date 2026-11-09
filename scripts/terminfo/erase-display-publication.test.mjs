// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is
// checked against the committed evidence. The publication is split into reviewable files (summary, run log, common frames with the report header, one
// file per case, run log, common frames and one file per read frame); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildEraseDisplayGradingReport } from './erase-display-grading.mjs';
import { eraseDisplayAdapterId } from './erase-display-grading-collector.mjs';
import { eraseDisplaySuiteId, eraseDisplayContracts } from './erase-display-contracts.mjs';

/** The predecessor suites' contracts: each contracts module exports exactly one `...Contracts` array of cases. */
const predecessorModules = ['ascii-sgr', 'sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline',
    'sgr-underline-color', 'sgr-reset', 'sgr-closure', 'erase-line'];
const predecessorContracts = (await Promise.all(predecessorModules.map(name => import(`./${name}-contracts.mjs`))))
    .flatMap(module => Object.entries(module).find(([key, value]) => key.endsWith('Contracts') && Array.isArray(value))[1]);

const dir = new URL('../../docs/qa/terminfo/2026-11-09-erase-display/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const ids = ['erase.screen.below', 'erase.screen.above', 'erase.screen.all'];
const load = () => {
    const summary = read('summary.json'), run = read('run-log.json'), common = read('journal-common.json'), header = common.reportHeader;
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of eraseDisplayContracts) {
        for (let index = 0; index < contract.roles.length; index++) {
            const journal = read(`journal-${contract.caseId}-read${index}.json`);
            assert.equal(journal.caseId, contract.caseId);
            assert.equal(journal.read, index);
            snapshots.push(journal.frame);
        }
    }
    snapshots.push(common.frames.finish);
    // The report header keeps the position of `cases` (null); the case files fill it in the contracted order.
    const cases = ids.map(id => read(`case-${id}.json`));
    return { summary, run, report: { ...header, cases, snapshots } };
};
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const observed = () => load().report.cases.map(item => item.observed);

test('the published erase-in-display evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildEraseDisplayGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, eraseDisplaySuiteId);
    assert.equal(report.adapter.id, eraseDisplayAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-char-edit-grading-v1');
    assert.equal(report.snapshots.length, 3 + 3 * 15);
    assert.deepEqual(report.cases.map(item => item.caseId), ids);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.pinned.observation.outcome]), ids.map(() => ['supported', 'supported']));
    for (const item of report.cases) assert.equal(item.divergence, undefined);
});

test('each case erased exactly the contracted rows, kept the rest and left the cursor at column 2 of row 20', () => {
    const [below, above, all] = observed();
    for (const item of [below, above, all]) {
        assert.deepEqual(item.geometry, { rows: 40, cols: 140 });
        assert.equal(item.reverseVideo, false);
        assert.equal(item.initial.ink, 0);
        assert.deepEqual(item.setup.cursor, [20, 2]);
        assert.deepEqual(item.erased.cursor, [20, 2]);
        assert.equal(item.modesUnchanged, true);
        assert.deepEqual(item.setup.text, ['AAAAA_____', 'BBBBB_____', 'CCCCC_____']);
        assert.deepEqual(item.pinnedRows, [0, 20, 39]);
    }
    assert.deepEqual(below.erased.text, ['AAAAA_____', 'BB________', '__________']);
    assert.deepEqual(above.erased.text, ['__________', '___BB_____', 'CCCCC_____']);
    assert.deepEqual(all.erased.text, ['__________', '__________', '__________']);
    assert.deepEqual([below.pinnedSequence, above.pinnedSequence, all.pinnedSequence], ['\x1b[0J', '\x1b[1J', '\x1b[2J']);
});

test('the pinned fixture bytes and responses are the geometry-dependent 40-row ones', () => {
    const { run, report } = load();
    const setup = '\x1b[1;1HAAAAA\x1b[21;1HBBBBB\x1b[40;1HCCCCC\x1b[21;3H';
    for (const [index, item] of report.cases.entries()) {
        assert.deepEqual(item.pinnedFeeds, [setup, ['\x1b[0J', '\x1b[1J', '\x1b[2J'][index]]);
        const response = JSON.parse(item.pinned.response);
        assert.deepEqual(response.rows, [0, 20, 39]);
        assert.deepEqual([response.cursorBefore, response.cursorAfter], [{ x: 2, y: 20 }, { x: 2, y: 20 }]);
        assert.deepEqual([response.scrollbackBefore, response.scrollbackAfter], [{ screenLines: 40, totalLines: 40, viewportY: 0 }, { screenLines: 40, totalLines: 40, viewportY: 0 }]);
        assert.ok(run.cases[index].feeds.includes(setup));
    }
});

test('spellings stay distinct: the pinned fixtures are explicit, the omitted spelling is a labeled observation that matches ED 0', () => {
    const [below, above, all] = observed().map(item => item.spellingControls);
    assert.equal(below.omitted.sequence, '\x1b[J');
    assert.equal(below.omitted.sameAsPinned, true);
    assert.equal(above.omitted.sameAsPinned, false);
    assert.equal(all.omitted.sameAsPinned, false);
    assert.equal(below.explicit1.sameAsPinned, false);
    assert.equal(above.explicit0.text[1], 'BB________');
    assert.equal(all.explicit0.text[2], '__________');
    for (const controls of [below, above, all]) {
        for (const [name, control] of Object.entries(controls).filter(([key]) => key !== 'policy')) assert.equal(control.matchesEcma48, true, name);
    }
});

test('unaffected rows and columns, the cursor-cell boundary and both screen edges hold in the fill and boundary controls', () => {
    const [below, above, all] = observed();
    for (const item of [below, above, all]) {
        assert.equal(item.fullScreen.before.text.length, 40);
        assert.deepEqual(item.fullScreen.before.cursor, [20, 4]);
        assert.deepEqual(item.fullScreen.after.cursor, [20, 4]);
        assert.deepEqual(item.fullScreen.before.text, Array.from({ length: 40 }, (_, row) => `ROW${String(row).padStart(2, '0')}-ABCD`));
    }
    assert.deepEqual(below.fullScreen.after.text.slice(18, 23), ['ROW18-ABCD', 'ROW19-ABCD', 'ROW2______', '__________', '__________']);
    assert.deepEqual(above.fullScreen.after.text.slice(18, 23), ['__________', '__________', '_____-ABCD', 'ROW21-ABCD', 'ROW22-ABCD']);
    assert.ok(all.fullScreen.after.text.every(row => row === '__________'));
    assert.deepEqual([below.boundary.cursor, above.boundary.cursor, all.boundary.cursor], [[20, 0], [20, 139], [39, 139]]);
    assert.deepEqual(below.boundary.after.text.slice(19, 22), ['ROW19-ABCD', '__________', '__________']);
    assert.deepEqual(above.boundary.after.text.slice(19, 22), ['__________', '__________', 'ROW21-ABCD']);
    for (const item of [below, above, all]) assert.deepEqual(item.boundary.after.cursor, item.boundary.cursor);
});

test('retained cells keep bold; background-color erase is observed (not required) over the whole erased region', () => {
    for (const item of load().report.cases) {
        const { attributes } = item.observed;
        assert.equal(attributes.retainedKeepBold, true);
        assert.match(attributes.policy, /observed, not required/);
        // The pen cell stores palette index 4; the erased region stores the same resolved blue as a literal: same color, different representation.
        assert.equal(attributes.backgroundColorErase, 'erased cells carry the current background color, stored differently from the pen cell (palette index versus literal ARGB)');
        const { pen, firstErased, lastErased, erasedCells, paintedCells } = attributes.backgroundColorEraseDetail;
        assert.deepEqual([pen.bgIndex, pen.backgroundArgb, firstErased.bgIndex, firstErased.backgroundArgb], [4, null, -1, 4278190318]);
        assert.deepEqual(pen.resolved, firstErased.resolved);
        assert.deepEqual(pen.resolved, lastErased.resolved);
        assert.equal(erasedCells, paintedCells);
        assert.ok(attributes.after.ink.some(entry => Array.isArray(entry.cols) && entry.char === ' '), 'a painted run is one range entry');
    }
    const [below, above, all] = load().report.cases.map(item => item.observed.attributes.backgroundColorEraseDetail.erasedCells);
    assert.deepEqual([below, above, all], [2798, 2803, 5600]);
});

test('viewport erasure is distinct from scrollback removal: the saved lines survive ED 0, ED 1 and ED 2 on the main screen', () => {
    for (const item of observed()) {
        const { history } = item;
        assert.equal(history.seeded, true);
        assert.equal(history.linesKept, true);
        assert.equal(history.linesAdded, 0);
        assert.equal(history.before.scrollback.alternateScreen, false);
        assert.deepEqual(history.before.scrollback.history, ['HIST-A', 'HIST-B', 'HIST-C', '']);
        assert.deepEqual(history.after.scrollback.history, history.before.scrollback.history);
        assert.equal(history.after.scrollback.scrollbackLines, 4);
        // The pinned fixtures themselves ran on the alternate screen, which has no saved lines.
        assert.deepEqual([item.setup.scrollback.alternateScreen, item.setup.scrollback.scrollbackLines], [true, 0]);
    }
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 26);
    assert.equal(receipt.eraseDisplayEvidence.suiteId, eraseDisplaySuiteId);
    assert.equal(receipt.eraseDisplayEvidence.runId, summary.context.runId);
    assert.deepEqual(receipt.eraseDisplayEvidence.counts, { supported: 3 });
    assert.equal(receipt.eraseDisplayEvidence.adapterBundleSha256, summary.context.eraseDisplayBundleSha256);
    assert.deepEqual(receipt.eraseLineEvidence.counts, { supported: 3 });
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.eraseDisplayEvidence.sha256, observationsHash);
    assert.equal(receipt.eraseDisplayEvidence.runSha256, runHash);
    assert.equal(summary.retained.eraseDisplayObservationsSha256, observationsHash);
    assert.equal(summary.retained.eraseDisplayRunSha256, runHash);
    assert.equal(summary.retained.eraseDisplayJournalSha256, journalHash);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v20');
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.ownedRunId, summary.context.runId);
    assert.notEqual(summary.stockCollectorRunId, summary.ownedRunId);
    // The predecessor inventory is derived from the merged contracts, not assumed; the erase-in-line batch is its own suite.
    const predecessors = predecessorContracts.map(contract => contract.caseId);
    const expected = new Set(predecessors);
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...expected].sort());
    assert.equal(unique.size, 37, 'the 37 unique handoff cases (all 32 pinned SGR ids plus the ASCII and erase-in-line cases)');
    assert.equal([...unique].filter(id => id.startsWith('sgr.')).length, 32);
    for (const suite of Object.values(summary.sameRun)) assert.ok(suite.cases.every(item => item.outcome === 'supported'));
    assert.ok(!ids.some(id => unique.has(id)), 'the three new cases are counted separately');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock run keeps its own grades: all three pinned cases are inconclusive there, and ED 3 is not touched', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of ids) assert.match(summary.stockCollector.probes[id].note, /^Erase pixel fixture needs capture/);
    for (const id of ids) assert.equal(summary.stockCollector.probes[id].outcome, 'inconclusive');
    assert.equal(summary.stockCollector.probes['erase.screen.scrollback'], undefined, 'the saved-lines case is not part of this publication');
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of readdirSync(dir)) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('prior publications still rederive beside this batch and every failed and superseded attempt is preserved', () => {
    const line = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-erase-line/summary.json', import.meta.url)));
    assert.equal(line.receipt.schemaVersion, 25);
    assert.equal(line.receipt.eraseDisplayEvidence, undefined);
    const { attempts, context } = load().summary;
    assert.equal(attempts.length, 5);
    assert.deepEqual(attempts.map(attempt => attempt.receiptCompleted), [true, true, true, false, false]);
    for (const attempt of attempts) assert.notEqual(attempt.runId, context.runId);
    const superseded = attempts.filter(attempt => attempt.verdicts);
    assert.equal(superseded.length, 3);
    assert.match(superseded[2].outcome, /blank cells skipped/);
    assert.match(superseded[0].outcome, /excluded the 15 seeded letters/);
    assert.deepEqual(Object.values(superseded[0].verdicts), ['supported', 'supported', 'supported']);
});
