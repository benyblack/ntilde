// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is
// checked against the committed evidence. The publication is split into reviewable files (summary, report header and
// per-case reports, run-log header and per-case runs, common frames and one file per read frame); the test reassembles
// the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildScrollbackGradingReport } from './scrollback-grading.mjs';
import { scrollbackAdapterId } from './scrollback-grading-collector.mjs';
import { scrollbackSuiteId, scrollbackCaseIds, makeScrollbackContracts } from './scrollback-contracts.mjs';
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
import { charEditContracts } from './char-edit-contracts.mjs';
import { eraseDisplayContracts } from './erase-display-contracts.mjs';
import { lineEditContracts } from './line-edit-contracts.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-09-scrollback/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const load = () => {
    const summary = read('summary.json'), header = read('report.json'), runHeader = read('run-log.json'), common = read('journal-common.json');
    const contracts = makeScrollbackContracts(common.frames.start.screen.rows, common.frames.start.screen.cols);
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of contracts) {
        for (let index = 0; index < contract.roles.length; index++) {
            const journal = read(`journal-${contract.caseId}-read${index}.json`);
            assert.equal(journal.caseId, contract.caseId);
            assert.equal(journal.read, index);
            snapshots.push(journal.frame);
        }
    }
    snapshots.push(common.frames.finish);
    const { caseIds, ...rest } = header;
    const report = { ...rest, cases: caseIds.map(id => read(`report-${id}.json`)), snapshots };
    const { caseIds: runIds, ...runRest } = runHeader;
    const run = { ...runRest, cases: runIds.map(id => read(`run-${id}.json`)) };
    return { summary, run, report, contracts };
};
const byId = (report, id) => report.cases.find(item => item.caseId === id);
const roleOf = (report, id, role) => byId(report, id).observed.roles[role];
const numbered = (prefix, from, to) => Array.from({ length: to - from + 1 }, (_, index) => `${prefix}${from + index}`);

test('the published scrolling/history evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildScrollbackGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, scrollbackSuiteId);
    assert.equal(report.adapter.id, scrollbackAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-line-editing-grading-v1');
    assert.equal(report.snapshots.length, 3 + 45);
    assert.deepEqual(report.cases.map(item => item.caseId), [...scrollbackCaseIds]);
    assert.deepEqual(report.cases.map(item => item.conclusion.outcome), scrollbackCaseIds.map(() => 'supported'));
});

test('deciding pinned probes agree with the contract; the three null pinned probes are recorded divergences with their own notes', () => {
    const { report } = load();
    const deciding = ['scrollback.accumulate', 'scrollback.total-lines', 'scrollback.scroll-up', 'scrollback.reverse-index', 'scrollback.scroll-down', 'scrollback.decstbm'];
    for (const id of deciding) {
        assert.equal(byId(report, id).pinned.observation.outcome, 'supported', id);
        assert.equal(byId(report, id).divergence, undefined, id);
    }
    const notes = { 'scrollback.set-region': 'Cursor position alone does not measure the scrolling region',
        'scrollback.alt-screen': 'Mode metadata does not measure preserved scrollback',
        'scrollback.decstbm-reset': 'Scrollback growth does not prove the earlier region was active before reset' };
    for (const [id, note] of Object.entries(notes)) {
        const item = byId(report, id);
        assert.equal(item.pinned.observation.outcome, 'inconclusive', id);
        assert.equal(item.pinned.note, note);
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'contract-measures-beyond-pinned' }, id);
    }
});

test('accumulation and total lines: empty history first, nothing before overflow, then exactly lines 0-10 and 51 total lines', () => {
    const { report } = load();
    for (const id of ['scrollback.accumulate', 'scrollback.total-lines']) {
        const item = byId(report, id);
        assert.deepEqual(item.observed.geometry, { rows: 40, cols: 140 });
        assert.equal(item.observed.reverseVideo, false);
        const initial = roleOf(report, id, 'initial'), full = roleOf(report, id, 'justFull'), final = roleOf(report, id, 'final');
        assert.equal(initial.historyCount, 0);
        assert.deepEqual(initial.viewport, []);
        assert.equal(full.historyCount, 0);
        assert.deepEqual(full.viewport, ['0-38: line 0..38']);
        assert.deepEqual(final.history, ['0-10: line 0..10']);
        assert.equal(final.historyCount, 11);
        assert.deepEqual(final.viewport, ['0-38: line 11..49']);
        assert.deepEqual(final.cursor, [39, 0]);
        const pinned = JSON.parse(item.pinned.response);
        assert.deepEqual([pinned.baseline.totalLines, pinned.scroll.totalLines], [40, 51]);
    }
});

test('SU, SD and RI moved exactly the region, in the right direction, with every outside row and the history intact', () => {
    const { report } = load();
    assert.deepEqual(roleOf(report, 'scrollback.scroll-up', 'seed').viewport, ['0: A', '1: B', '2: C', '3: D', '4: E']);
    assert.deepEqual(roleOf(report, 'scrollback.scroll-up', 'seed').margins, [1, 3]);
    assert.deepEqual(roleOf(report, 'scrollback.scroll-up', 'after').viewport, ['0: A', '1: C', '2: D', '4: E']);
    assert.equal(roleOf(report, 'scrollback.scroll-up', 'after').historyCount, 0);
    assert.deepEqual(roleOf(report, 'scrollback.scroll-up', 'restored').margins, [0, 39]);
    assert.equal(roleOf(report, 'scrollback.scroll-up', 'wideAfter').historyCount, 0);
    assert.deepEqual(roleOf(report, 'scrollback.reverse-index', 'before').viewport, ['0: A', '1: B', '2: C']);
    assert.deepEqual(roleOf(report, 'scrollback.reverse-index', 'after').viewport, ['1: A', '2: B', '3: C']);
    assert.deepEqual(roleOf(report, 'scrollback.reverse-index', 'moveOnly').viewport, ['0: A', '1: B', '2: C']);
    assert.deepEqual(roleOf(report, 'scrollback.reverse-index', 'regionAfter').viewport, ['0: A', '2: B', '3: C', '4: E']);
    assert.deepEqual(roleOf(report, 'scrollback.scroll-down', 'after').viewport, ['1-3: LINE1..3']);
    assert.deepEqual(roleOf(report, 'scrollback.scroll-down', 'after').cursor, [2, 5]);
    assert.deepEqual(roleOf(report, 'scrollback.scroll-down', 'regionAfter').viewport, ['0: A', '2: B', '3: C', '4: E']);
    assert.equal(roleOf(report, 'scrollback.scroll-down', 'fullAfter').historyCount, 0);
    for (const id of ['scrollback.scroll-up', 'scrollback.reverse-index', 'scrollback.scroll-down']) {
        for (const role of Object.values(byId(report, id).observed.roles)) assert.equal(role.matchesModel, true, id);
    }
});

test('margins, DECSTBM and the reset: boundaries observed, scrolling confined, fixed rows kept, reset restores history growth', () => {
    const { report } = load();
    assert.deepEqual(roleOf(report, 'scrollback.set-region', 'set').margins, [4, 9]);
    assert.deepEqual(roleOf(report, 'scrollback.set-region', 'set').cursor, [0, 0]);
    assert.deepEqual(roleOf(report, 'scrollback.set-region', 'reset').margins, [0, 39]);
    assert.equal(roleOf(report, 'scrollback.set-region', 'ctlScrolled').historyCount, 0);
    assert.deepEqual(roleOf(report, 'scrollback.set-region', 'ctlFull').history, ['0: ROW-0']);
    const after = roleOf(report, 'scrollback.decstbm', 'after');
    assert.deepEqual(after.viewport, ['0: FIXED_TOP', '8: Z']);
    assert.deepEqual([after.margins, after.cursor, after.historyCount], [[2, 9], [9, 0], 0]);
    assert.deepEqual(roleOf(report, 'scrollback.decstbm', 'innerBefore').viewport, ['0: FIXED_TOP', '2: INNER']);
    assert.equal(roleOf(report, 'scrollback.decstbm', 'ctlAfter').historyCount, 0);
    const reset = byId(report, 'scrollback.decstbm-reset').observed.roles;
    assert.deepEqual(reset.set.margins, [4, 9]);
    assert.deepEqual(reset.reset.margins, [0, 39]);
    assert.deepEqual(reset.final.history, ['0-10: line-0..10']);
    assert.deepEqual(reset.final.viewport, ['0-38: line-11..49']);
    assert.equal(reset.ctlRegion.historyCount, 0, 'an unreset region keeps history empty');
    assert.deepEqual(reset.ctlRegion.viewport.slice(0, 1), ['0-3: line-0..3']);
    assert.deepEqual(numbered('line-', 0, 1), ['line-0', 'line-1']);
});

test('alternate screen: seeded main history, isolation while alternate, exact restoration, and the observed alternate-screen details', () => {
    const { report } = load();
    const roles = byId(report, 'scrollback.alt-screen').observed.roles;
    assert.deepEqual(roles.seeded.history, ['0-2: ROW-0..2']);
    assert.deepEqual(roles.normal.viewport, ['0-36: ROW-3..39', '39: NORMAL']);
    assert.equal(roles.alt.screen, 'alternate');
    assert.deepEqual(roles.alt.viewport, []);
    // Observed, not required: the alternate screen reports the main history and homes the cursor on entry.
    assert.equal(roles.alt.historyCount, 3);
    assert.deepEqual(roles.alt.cursor, [0, 0]);
    assert.deepEqual(roles.altOverflow.viewport, ['0-38: ALT-6..44']);
    assert.equal(roles.altOverflow.historyCount, 3);
    assert.equal(roles.restored.screen, 'main');
    assert.deepEqual(roles.restored.viewport, roles.normal.viewport);
    assert.deepEqual(roles.restored.cursor, roles.normal.cursor);
    assert.deepEqual(roles.restored.history, roles.seeded.history);
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 29);
    assert.equal(receipt.scrollbackEvidence.suiteId, scrollbackSuiteId);
    assert.equal(receipt.scrollbackEvidence.runId, summary.context.runId);
    assert.deepEqual(receipt.scrollbackEvidence.counts, { supported: 9 });
    assert.equal(receipt.scrollbackEvidence.adapterBundleSha256, summary.context.scrollbackBundleSha256);
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.scrollbackEvidence.sha256, observationsHash);
    assert.equal(receipt.scrollbackEvidence.runSha256, runHash);
    assert.equal(summary.retained.scrollbackObservationsSha256, observationsHash);
    assert.equal(summary.retained.scrollbackRunSha256, runHash);
    assert.equal(summary.retained.scrollbackJournalSha256, journalHash);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v23');
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.ownedRunId, summary.context.runId);
    assert.notEqual(summary.stockCollectorRunId, summary.ownedRunId);
    // The predecessor inventory is derived from the merged contracts, not assumed.
    const expected = new Set([...asciiSgrContracts, ...sgrColorsContracts, ...sgrRgbContracts, ...sgrAttrsContracts, ...sgrPaletteContracts,
        ...sgrTruecolorContracts, ...sgrUnderlineContracts, ...sgrUnderlineColorContracts, ...sgrResetContracts, ...sgrClosureContracts,
        ...eraseLineContracts, ...charEditContracts, ...eraseDisplayContracts, ...lineEditContracts].map(contract => contract.caseId));
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...expected].sort());
    for (const suite of Object.values(summary.sameRun)) assert.ok(suite.cases.every(item => item.outcome === 'supported'));
    assert.ok(!scrollbackCaseIds.some(id => unique.has(id)), 'the nine new cases are counted separately');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock run keeps its own grades: all nine pinned cases are inconclusive there', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of scrollbackCaseIds) assert.equal(summary.stockCollector.probes[id].outcome, 'inconclusive', id);
    assert.match(summary.stockCollector.probes['scrollback.accumulate'].note, /^Cursor position does not measure scrollback history/);
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of readdirSync(dir)) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('prior publications still rederive beside this batch', () => {
    const erase = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-erase-line/summary.json', import.meta.url)));
    assert.equal(erase.receipt.schemaVersion, 25);
    assert.equal(erase.receipt.scrollbackEvidence, undefined);
    const chars = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-09-char-edit/summary.json', import.meta.url)));
    assert.equal(chars.receipt.schemaVersion, 26);
    assert.equal(chars.receipt.scrollbackEvidence, undefined);
    const display = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-09-erase-display/summary.json', import.meta.url)));
    assert.equal(display.receipt.schemaVersion, 27);
    assert.equal(display.receipt.scrollbackEvidence, undefined);
    const lines = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-09-line-editing/summary.json', import.meta.url)));
    assert.equal(lines.receipt.schemaVersion, 28);
    assert.equal(lines.receipt.scrollbackEvidence, undefined);
    const { attempts, context } = load().summary;
    assert.equal(attempts.length, 4);
    for (const attempt of attempts) assert.notEqual(attempt.runId, context.runId);
    for (const attempt of attempts) assert.deepEqual(Object.values(attempt.verdicts), scrollbackCaseIds.map(() => 'supported'));
});
