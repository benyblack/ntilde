// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is
// checked against the committed evidence. The publication is split into reviewable files (summary, report, run
// log, common frames and one file per read frame); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildCharEditGradingReport } from './char-edit-grading.mjs';
import { charEditAdapterId } from './char-edit-grading-collector.mjs';
import { charEditSuiteId, charEditContracts } from './char-edit-contracts.mjs';
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
import { eraseLineContracts } from './erase-line-contracts.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-09-char-edit/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const ICH = 'editing.insert-chars', DCH = 'editing.delete-chars', REP = 'editing.repeat-char';
const ids = [ICH, DCH, REP];
const load = () => {
    const summary = read('summary.json'), report = read('report.json'), run = read('run-log.json'), common = read('journal-common.json');
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of charEditContracts) {
        for (let index = 0; index < contract.roles.length; index++) {
            const journal = read(`journal-${contract.caseId}-read${index}.json`);
            assert.equal(journal.caseId, contract.caseId);
            assert.equal(journal.read, index);
            snapshots.push(journal.frame);
        }
    }
    snapshots.push(common.frames.finish);
    return { summary, run, report: { ...report, snapshots } };
};
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const observed = id => load().report.cases.find(item => item.caseId === id).observed;

test('the published character-editing evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildCharEditGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, charEditSuiteId);
    assert.equal(report.adapter.id, charEditAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-erase-line-grading-v1');
    assert.equal(report.snapshots.length, 3 + 3 * 15);
    assert.deepEqual(report.cases.map(item => item.caseId), ids);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.pinned.observation.outcome]), ids.map(() => ['supported', 'supported']));
    for (const item of report.cases) assert.equal(item.divergence, undefined);
});

test('the exact pinned fixture bytes were fed and the pinned code ran over the observed screens', () => {
    const { report, run } = load();
    const seed = '\x1b[1;1H\x1b[2KABCDEZQH';
    assert.deepEqual(report.cases.map(item => [...item.pinnedFeeds]), [
        [seed, '\x1b[1;3H', '\x1b[1@'], [seed, '\x1b[1;3H', '\x1b[1P'], ['\x1b[1;1H\x1b[2KAX   Z', '\x1b[1;2H', 'X', '\x1b[3b']]);
    for (const [index, item] of report.cases.entries()) {
        for (const feed of item.pinnedFeeds) assert.ok(run.cases[index].feeds.some(recorded => recorded === feed || recorded.endsWith(feed)), JSON.stringify(feed));
    }
    // REP: the graphic and its repeat travel in ONE fenced write, so no DA barrier separates them.
    assert.ok(run.cases[2].feeds.includes('X\x1b[3b'));
    assert.match(report.cases[2].pinned.response, /"after":\["A","X","X","X","X","Z"\]/);
});

test('ICH and DCH moved exactly the contracted cells, left the cursor at column 2, and the whole row agrees', () => {
    const ich = observed(ICH), dch = observed(DCH);
    for (const item of [ich, dch]) {
        assert.deepEqual(item.geometry, { rows: 40, cols: 140 });
        assert.equal(item.reverseVideo, false);
        assert.equal(item.initial.ink, 0);
        assert.deepEqual(item.seed.cursor, [0, 8]);
        assert.deepEqual(item.setup.cursor.cursor, [0, 2]);
        assert.equal(item.seed.text[0], 'ABCDEZQH____');
        assert.deepEqual(item.pinned.cursor, [0, 2]);
    }
    assert.equal(ich.pinnedSequence, '\x1b[1@');
    assert.equal(dch.pinnedSequence, '\x1b[1P');
    assert.equal(ich.pinned.text[0], 'AB_CDEZQH___');
    assert.equal(dch.pinned.text[0], 'ABDEZQH_____');
});

test('REP repeated the independently established X exactly three times and kept the flank and cursor', () => {
    const rep = observed(REP);
    assert.equal(rep.pinnedSequence, 'X\x1b[3b');
    assert.equal(rep.seed.text[0], 'AX___Z______');
    assert.deepEqual(rep.setup.target.cursor, [0, 1]);
    assert.deepEqual(rep.setup.before.cursor, [0, 2]);
    assert.equal(rep.setup.before.text[0], 'AX___Z______');
    assert.equal(rep.pinned.text[0], 'AXXXXZ______');
    assert.deepEqual(rep.pinned.cursor, [0, 5]);
    assert.equal(rep.wideRow.after.text[1], 'AXXXXZQRST__');
    assert.deepEqual(rep.wideRow.after.cursor, [1, 5]);
});

test('REP after a fence barrier repeats nothing: ECMA-48 leaves REP undefined after a control function and the App clears the repeatable character', () => {
    const { splitControl } = observed(REP);
    assert.equal(splitControl.repeats, false);
    assert.equal(splitControl.text[0], 'AX___Z______');
    assert.match(splitControl.policy, /not graded/);
});

test('spellings stay distinct: omitted and explicit 0 agree with explicit 1, counts 2, 5 and a huge count differ, and the pinned spelling is the one graded', () => {
    const [ich, dch, rep] = ids.map(id => observed(id).spellingControls);
    for (const controls of [ich, dch]) {
        assert.equal(controls.omitted.sameAsPinned, true);
        assert.equal(controls.explicit0.sameAsPinned, true);
        assert.equal(controls.explicit2.sameAsPinned, false);
        assert.equal(controls.explicit999.text[0], 'AB__________');
    }
    assert.equal(ich.explicit2.text[0], 'AB__CDEZQH__');
    assert.equal(dch.explicit2.text[0], 'ABEZQH______');
    assert.deepEqual([rep.omitted.text[0], rep.explicit0.text[0], rep.explicit1.text[0], rep.explicit5.text[0]],
        ['AXX__Z______', 'AXX__Z______', 'AXX__Z______', 'AXXXXXX_____']);
    for (const controls of [ich, dch, rep]) {
        for (const [name, control] of Object.entries(controls).filter(([key]) => key !== 'policy')) assert.equal(control.matchesEcma48, true, name);
    }
});

test('unaffected rows and columns, and the whole-row shift, hold in the wide-row control', () => {
    const [ich, dch] = [ICH, DCH].map(id => observed(id).wideRow);
    for (const wide of [ich, dch]) {
        assert.deepEqual(wide.before.text.slice(0, 1), ['ABOVE-ROW!__']);
        assert.deepEqual(wide.before.cursor, [1, 2]);
        assert.deepEqual(wide.after.cursor, [1, 2]);
        assert.equal(wide.after.text[0], 'ABOVE-ROW!__');
        assert.equal(wide.after.text[2], 'BELOW-ROW!__');
    }
    assert.deepEqual([ich.after.text[1], dch.after.text[1]], ['AB_CDEFGHIJ_', 'ABDEFGHIJ___']);
});

test('right edge: ICH truncates at the last column and DCH exposes a blank at it', () => {
    const [ich, dch] = [ICH, DCH].map(id => observed(id).rightEdge);
    assert.equal(ich.before.tail, '___WXY');
    assert.equal(dch.before.tail, '___WXY');
    assert.equal(ich.after.tail, '____WX');
    assert.equal(dch.after.tail, '___XY_');
    assert.deepEqual([ich.after.cursor, dch.after.cursor], [[0, 137], [0, 137]]);
});

test('attributes: moved cells keep bold; created blanks and repeated cells are observed against the same-pen reference', () => {
    for (const id of [ICH, DCH]) {
        const { attributes } = observed(id);
        assert.equal(attributes.movedKeepAttributes, true);
        assert.match(attributes.policy, /not required/);
        assert.equal(attributes.createdBlankBackground, 'created cells carry the current background');
        const { pen, first } = attributes.createdBlankBackgroundDetail;
        assert.deepEqual([pen.bgIndex, pen.backgroundArgb, first.bgIndex, first.backgroundArgb], [4, null, 4, null]);
        assert.deepEqual(pen.resolved, first.resolved);
    }
    const { attributes } = observed(REP);
    assert.equal(attributes.flanksNeutral, true);
    assert.equal(attributes.repeatedCells, 'repeated cells carry the current pen attributes');
    assert.match(attributes.policy, /not required/);
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 26);
    assert.equal(receipt.charEditEvidence.suiteId, charEditSuiteId);
    assert.equal(receipt.charEditEvidence.runId, summary.context.runId);
    assert.deepEqual(receipt.charEditEvidence.counts, { supported: 3 });
    assert.equal(receipt.charEditEvidence.adapterBundleSha256, summary.context.charEditBundleSha256);
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.charEditEvidence.sha256, observationsHash);
    assert.equal(receipt.charEditEvidence.runSha256, runHash);
    assert.equal(summary.retained.charEditObservationsSha256, observationsHash);
    assert.equal(summary.retained.charEditRunSha256, runHash);
    assert.equal(summary.retained.charEditJournalSha256, journalHash);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v20');
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.ownedRunId, summary.context.runId);
    // The predecessor inventory is derived from the merged contracts, not assumed.
    const expected = new Set([...asciiSgrContracts, ...sgrColorsContracts, ...sgrRgbContracts, ...sgrAttrsContracts, ...sgrPaletteContracts,
        ...sgrTruecolorContracts, ...sgrUnderlineContracts, ...sgrUnderlineColorContracts, ...sgrResetContracts, ...sgrClosureContracts,
        ...eraseLineContracts].map(contract => contract.caseId));
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...expected].sort());
    for (const suite of Object.values(summary.sameRun)) assert.ok(suite.cases.every(item => item.outcome === 'supported'));
    assert.ok(Object.hasOwn(summary.sameRun, 'eraseLine'));
    assert.ok(!ids.some(id => unique.has(id)), 'the three new cases are counted separately');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the stock collector is untouched and was not re-run: its facts are carried and labeled as carried', () => {
    const { stockCollector } = load().summary;
    assert.equal(stockCollector.rerun, false);
    assert.deepEqual(stockCollector.carriedCounts, { inconclusive: 168, supported: 86, unsupported: 1 });
    for (const id of ids) {
        assert.equal(stockCollector.probes[id].outcome, 'inconclusive');
        assert.equal(stockCollector.probes[id].evidence, 'query');
    }
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of readdirSync(dir)) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1800, `${name} has ${lines} lines`);
    }
});

test('prior publications still rederive beside this batch and the failed and superseded attempts are preserved', () => {
    const erase = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-erase-line/summary.json', import.meta.url)));
    assert.equal(erase.receipt.schemaVersion, 25);
    assert.equal(erase.receipt.charEditEvidence, undefined);
    const { attempts, context } = load().summary;
    assert.equal(attempts.length, 3);
    assert.deepEqual(attempts.map(attempt => attempt.receiptCompleted), [false, true, true]);
    assert.match(attempts[0].outcome, /Unexpected input during cell collection/);
    for (const attempt of attempts) assert.notEqual(attempt.runId, context.runId);
    assert.match(attempts[1].outcome, /flank cells' columns but not their rows/);
    assert.deepEqual(Object.values(attempts[1].verdicts), ['supported', 'supported', 'supported']);
    assert.match(attempts[2].outcome, /overline, underline style and underline color/);
    assert.deepEqual(Object.values(attempts[2].verdicts), ['supported', 'supported', 'supported']);
});
