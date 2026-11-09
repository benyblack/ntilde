// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked against the
// committed evidence. The publication is split into reviewable files (summary, run log, common frames with the report header, one
// file per case and one file per read frame); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildEraseAttrsRegionGradingReport } from './erase-attrs-region-grading.mjs';
import { eraseAttrsRegionAdapterId } from './erase-attrs-region-grading-collector.mjs';
import { eraseAttrsRegionSuiteId, eraseAttrsRegionContracts, eraseAttrsRegionCaseIds } from './erase-attrs-region-contracts.mjs';
import { eraseAttrsRegionCounts } from './erase-attrs-region-receipt.mjs';
import { benchmarkSuites } from './qualification-inventory.mjs';

const EL = 'erase.el-with-attrs', ED = 'erase.ed-scroll-region';
const dir = new URL('../../docs/qa/terminfo/2026-11-09-erase-attrs-region/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const load = () => {
    const summary = read('summary.json'), run = read('run-log.json'), common = read('journal-common.json'), header = common.reportHeader;
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of eraseAttrsRegionContracts) {
        for (let index = 0; index < contract.roles.length; index++) {
            const journal = read(`journal-${contract.caseId}-read${index}.json`);
            assert.equal(journal.caseId, contract.caseId);
            assert.equal(journal.read, index);
            snapshots.push(journal.frame);
        }
    }
    snapshots.push(common.frames.finish);
    // The report header keeps the position of `cases` (null); the case files fill it in the contracted order.
    const cases = eraseAttrsRegionCaseIds.map(id => read(`case-${id}.json`));
    return { summary, run, report: { ...header, cases, snapshots } };
};
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const observed = () => load().report.cases.map(item => item.observed);
const green = { r: 0, g: 205, b: 0 }, blue = { r: 0, g: 0, b: 238 };

test('the published evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildEraseAttrsRegionGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, eraseAttrsRegionSuiteId);
    assert.equal(report.adapter.id, eraseAttrsRegionAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-saved-reset-grading-v1');
    assert.equal(report.snapshots.length, 3 + 11 + 7);
    assert.deepEqual(report.cases.map(item => item.caseId), [EL, ED]);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.pinned.observation.outcome]), [['supported', 'supported'], ['supported', 'supported']]);
    for (const item of report.cases) assert.equal(item.divergence, undefined, 'pinned and contract verdicts agree');
});

test('the pinned fixture bytes, pinned responses and recorded feeds are the exact upstream ones', () => {
    const { run, report } = load();
    const [el, ed] = report.cases;
    assert.deepEqual(el.pinnedFeeds, ['\x1b[42m\x1b[1;1HXXXXX\x1b[1;1H', '\x1b[K', '\x1b[0m']);
    assert.deepEqual(ed.pinnedFeeds, ['\x1b[1;1HKEEP!\x1b[3;1HERASE\x1b[3;10r\x1b[3;1H', '\x1b[J', '\x1b[r']);
    for (const [index, item] of report.cases.entries()) for (const feed of item.pinnedFeeds) assert.ok(run.cases[index].feeds.includes(feed), JSON.stringify(feed));
    const elResponse = JSON.parse(el.pinned.response);
    assert.deepEqual(Object.keys(elResponse), ['before', 'after']);
    assert.equal(elResponse.before.char, 'X');
    assert.deepEqual([elResponse.before.bg, elResponse.after.bg], [green, green]);
    assert.equal(elResponse.after.char, ' ');
    assert.equal(ed.pinned.response, JSON.stringify({ before: { control: [...'KEEP!'], target: [...'ERASE'], cursor: { x: 0, y: 2, visible: true } },
        after: { control: [...'KEEP!'], target: [' ', ' ', ' ', ' ', ' '] } }));
});

test('erase.el-with-attrs: the X carried the palette green, EL left blank cells with the same RESOLVED background, stored as a literal ARGB', () => {
    const [el] = observed();
    assert.deepEqual(el.geometry, { rows: 40, cols: 140 });
    assert.equal(el.reverseVideo, false);
    assert.equal(el.initial.ink, 0);
    assert.deepEqual(el.paletteGreen, green);
    assert.equal(el.setup.row0, 'XXXXX_____');
    assert.deepEqual(el.setup.cursor, [0, 0]);
    assert.deepEqual(el.erased.cursor, [0, 0]);
    assert.equal(el.erased.row0, '__________');
    assert.equal(el.modesUnchanged, true);
    const { background } = el;
    assert.match(background.policy, /RESOLVED background RGB/);
    // Before: palette index 2 resolved to the App's green. After: the same resolved color stored as a literal ARGB (0xFF00CD00), not index 2.
    assert.deepEqual(background.before, { bgIndex: 2, backgroundArgb: null, resolved: green, source: 'palette' });
    assert.deepEqual(background.after, { bgIndex: -1, backgroundArgb: 0xff00cd00, resolved: green, source: 'literal' });
    assert.equal(background.resolvedMatchesBefore, true, 'the pinned criterion is resolved-RGB equality');
    assert.equal(background.storedRepresentationMatches, false, 'palette versus literal is observed, not required');
    assert.deepEqual([background.erasedCells, background.paintedCells, background.sameResolvedAsBefore], [140, 140, 140]);
    assert.deepEqual(background.lastErased, background.after);
});

test('erase.el-with-attrs: the pen was restored and the full-screen control saw no whole-screen clearing', () => {
    const [el] = observed();
    assert.equal(el.restoration.witnessNeutral, true);
    assert.equal(el.restoration.witness.row0, 'W_________');
    assert.equal(el.fullScreen.before.seeded, true);
    assert.equal(el.fullScreen.before.row0, 'XXXXX_____');
    assert.equal(el.fullScreen.after.markersKept, true);
    assert.equal(el.fullScreen.after.row0, '__________');
    assert.deepEqual([el.fullScreen.before.cursor, el.fullScreen.after.cursor], [[0, 0], [0, 0]]);
});

test('erase.el-with-attrs: the erase follows the pen active at erase time, observed without being required', () => {
    const [el] = observed();
    const { penChanged, penReset } = el.penControls;
    assert.match(el.penControls.policy, /observed, not graded/);
    assert.equal(penChanged.seeded, true);
    assert.deepEqual(penChanged.background.before.resolved, green);
    assert.deepEqual(penChanged.background.after.resolved, blue, 'with SGR 44 active the erased cells take the current pen (blue), not the green they replaced');
    assert.equal(penChanged.background.resolvedMatchesBefore, false);
    assert.equal(penReset.seeded, true);
    assert.equal(penReset.background.after, null, 'with SGR 0 active the erased cells carry the default background');
    assert.equal(penReset.background.paintedCells, 0);
    assert.equal(el.spellingControls.explicit0.sameAsPinned, true, 'the explicit-0 spelling is the same erase');
});

test('erase.ed-scroll-region: KEEP! survived, ERASE read blank, the cursor and the margins were observed and unchanged', () => {
    const [, ed] = observed();
    assert.deepEqual(ed.geometry, { rows: 40, cols: 140 });
    assert.equal(ed.reverseVideo, false);
    assert.equal(ed.initial.ink, 0);
    assert.deepEqual(ed.initial.margins, [0, 39]);
    assert.deepEqual(ed.setup.text, ['KEEP!_____', 'ERASE_____']);
    assert.deepEqual([ed.setup.cursor, ed.setup.margins, ed.setup.originMode], [[2, 0], [2, 9], false], 'CSI 3;10 r configured the margins and CSI 3;1 H put the cursor at row 2 column 0');
    assert.deepEqual(ed.erased.text, ['KEEP!_____', '__________']);
    assert.deepEqual(ed.erased.ink, ['0,0,K', '0,1,E', '0,2,E', '0,3,P', '0,4,!'], 'the whole screen holds only KEEP! afterwards');
    assert.deepEqual([ed.erased.cursor, ed.erased.margins, ed.erased.originMode], [[2, 0], [2, 9], false]);
    assert.equal(ed.modesUnchanged, true);
    assert.match(ed.preserved.policy, /whole screen is exactly KEEP!/);
});

test('erase.ed-scroll-region: the pinned CSI r restored the margins; the cursor after it and ED at the margins are observations only', () => {
    const [, ed] = observed();
    assert.equal(ed.restoration.marginsRestored, true);
    assert.deepEqual(ed.restoration.restored.margins, [0, 39]);
    assert.deepEqual(ed.restoration.restored.cursor, [0, 0], 'observed: CSI r homed the cursor; not a requirement');
    assert.equal(ed.spellingControls.explicit0.sameAsPinned, true);
    const { boundary } = ed;
    assert.match(boundary.policy, /observed, not graded, no standards claim/);
    assert.equal(boundary.seeded, true);
    assert.deepEqual([boundary.cursor, boundary.margins], [[2, 0], [2, 9]]);
    assert.deepEqual(boundary.rowsKept, { first: 0, last: 1, count: 2 });
    assert.equal(boundary.partialRows, 0, 'every row is either whole or blank');
    // ED 0 erased the rows inside the region and, in this measurement, every row below the bottom margin too; the pinned criterion does not ask either.
    assert.equal(boundary.keptAboveCursor, true);
    assert.equal(boundary.erasedInsideRegion, true);
    assert.equal(boundary.erasedBelowBottomMargin, true);
    assert.equal(boundary.modesUnchanged, true);
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', async () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 32);
    assert.equal(receipt.eraseAttrsRegionEvidence.suiteId, eraseAttrsRegionSuiteId);
    assert.equal(receipt.eraseAttrsRegionEvidence.runId, summary.context.runId);
    const { counts } = eraseAttrsRegionCounts(report);
    assert.deepEqual(counts, { supported: 2 });
    assert.deepEqual(receipt.eraseAttrsRegionEvidence.counts, counts);
    assert.equal(receipt.eraseAttrsRegionEvidence.adapterBundleSha256, summary.context.eraseAttrsRegionBundleSha256);
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.eraseAttrsRegionEvidence.sha256, observationsHash);
    assert.equal(receipt.eraseAttrsRegionEvidence.runSha256, runHash);
    assert.equal(summary.retained.eraseAttrsRegionObservationsSha256, observationsHash);
    assert.equal(summary.retained.eraseAttrsRegionRunSha256, runHash);
    assert.equal(summary.retained.eraseAttrsRegionJournalSha256, journalHash);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v26');
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.ownedRunId, summary.context.runId);
    assert.notEqual(summary.stockCollectorRunId, summary.ownedRunId);
    // The predecessor inventory is derived from the merged contracts, not assumed: every other benchmark suite's case ids.
    const expected = [];
    for (const suite of benchmarkSuites.filter(item => item.suite !== 'erase-attrs-region')) {
        const module = await import(new URL(suite.module, import.meta.url));
        expected.push(...module[suite.exports[2]]);
    }
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...new Set(expected)].sort());
    assert.equal(Object.keys(summary.sameRun).length, benchmarkSuites.length - 1, 'one same-run entry per merged predecessor suite');
    for (const [name, suite] of Object.entries(summary.sameRun)) {
        // The saved-state/reset suite keeps its two pinned-inconclusive cases; its separately identified effect contracts are all supported.
        if (name === 'saved-reset') assert.ok(suite.cases.every(item => item.effectContract === 'supported'), name);
        else assert.ok(suite.cases.every(item => item.outcome === 'supported'), name);
    }
    assert.ok(![EL, ED].some(id => unique.has(id)), 'the two new cases are counted separately');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock run keeps its own grades: both pinned cases are inconclusive there (cursor-query path)', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of [EL, ED]) {
        assert.deepEqual([summary.stockCollector.probes[id].outcome, summary.stockCollector.probes[id].evidence], ['inconclusive', 'query'], id);
    }
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of readdirSync(dir)) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('prior publications still rederive beside this batch and every superseded attempt is preserved', () => {
    const earlier = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-09-saved-reset/summary.json', import.meta.url)));
    assert.equal(earlier.receipt.schemaVersion, 31);
    assert.equal(earlier.receipt.eraseAttrsRegionEvidence, undefined);
    const { attempts, context } = load().summary;
    assert.equal(attempts.length, 2);
    assert.equal(attempts[1].runId, context.runId);
    assert.notEqual(attempts[0].runId, context.runId);
    assert.deepEqual(attempts.map(attempt => attempt.receiptCompleted), [true, true]);
    assert.deepEqual(Object.values(attempts[0].verdicts), ['supported', 'supported']);
    assert.match(attempts[0].outcome, /masked as an inconclusive restoration failure/);
    assert.match(attempts[0].outcome, /pen witness was written wherever the erase left the cursor/);
});
