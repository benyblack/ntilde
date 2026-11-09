// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked against the committed
// evidence. The publication is split into reviewable files (summary, report header and per-case reports, run-log header and per-case runs,
// common frames and one file per read frame); the test reassembles the exact journal order and rederives every conclusion from it.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildEraseCharsHistoryGradingReport } from './erase-chars-history-grading.mjs';
import { eraseCharsHistoryAdapterId } from './erase-chars-history-grading-collector.mjs';
import { eraseCharsHistorySuiteId, eraseCharsHistoryCaseIds, makeEraseCharsHistoryContracts } from './erase-chars-history-contracts.mjs';
import { benchmarkSuites } from './qualification-inventory.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-09-erase-chars-history/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const load = () => {
    const summary = read('summary.json'), header = read('report.json'), runHeader = read('run-log.json'), common = read('journal-common.json');
    const contracts = makeEraseCharsHistoryContracts(common.frames.start.screen.rows, common.frames.start.screen.cols);
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

test('the published erase-character / saved-lines evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildEraseCharsHistoryGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, eraseCharsHistorySuiteId);
    assert.equal(report.adapter.id, eraseCharsHistoryAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-erase-attrs-region-grading-v1');
    assert.equal(report.snapshots.length, 3 + 15 + 10);
    assert.deepEqual(report.cases.map(item => item.caseId), [...eraseCharsHistoryCaseIds]);
    assert.deepEqual(report.cases.map(item => item.conclusion.outcome), ['supported', 'supported']);
    // Each case names the layer it measured: ECH stored cells and attributes; ED 3 stored cells plus the actual saved lines.
    assert.equal(byId(report, 'erase.character').conclusion.evidence, 'owned-stored-cells');
    assert.equal(byId(report, 'erase.screen.scrollback').conclusion.evidence, 'owned-stored-cells-and-actual-scrollback');
});

test('the pinned probes and the contract agree on both exact ids; nothing diverges', () => {
    const { report } = load();
    for (const id of eraseCharsHistoryCaseIds) {
        assert.equal(byId(report, id).pinned.observation.outcome, 'supported', id);
        assert.equal(byId(report, id).divergence, undefined, id);
    }
    assert.deepEqual(byId(report, 'erase.character').pinnedFeeds, ['ABCDE\x1b[1G', '\x1b[3X']);
    assert.deepEqual(byId(report, 'erase.screen.scrollback').pinnedFeeds, ['\r\n'.repeat(42), '\x1b[3J']);
    assert.equal(byId(report, 'erase.character').pinned.response, JSON.stringify({ before: [...'ABCDE'], cursorBefore: { x: 0, y: 0 }, after: [' ', ' ', ' ', 'D', 'E'] }));
    assert.equal(byId(report, 'erase.screen.scrollback').pinned.response,
        JSON.stringify({ before: { screenLines: 40, totalLines: 43, viewportY: 0 }, after: { screenLines: 40, totalLines: 40, viewportY: 0 } }));
    assert.equal(byId(report, 'erase.character').pinned.assertions[0].expected, 'row 0 blank,blank,blank,D,E; cursor before 0,0');
});

test('ECH: exactly cells 0..2 were erased in place, D and E kept, the row, rows around it and the cursor untouched', () => {
    const { report } = load();
    const id = 'erase.character';
    assert.deepEqual(byId(report, id).observed.geometry, { rows: 40, cols: 140 });
    assert.equal(byId(report, id).observed.reverseVideo, false);
    assert.deepEqual(roleOf(report, id, 'initial').viewport, []);
    assert.deepEqual(roleOf(report, id, 'seed').viewport, ['0: ABCDE']);
    assert.deepEqual(roleOf(report, id, 'seed').cursor, [0, 0]);
    assert.deepEqual(roleOf(report, id, 'erased').viewport, ['0:    DE']);
    assert.deepEqual(roleOf(report, id, 'erased').cursor, [0, 0]);
    assert.deepEqual(roleOf(report, id, 'wide').viewport, ['0: ABOVE-ROW!', '1: AB   FGHIJ', '2: BELOW-ROW!']);
    assert.deepEqual(roleOf(report, id, 'wide').cursor, [1, 2]);
    assert.deepEqual(roleOf(report, id, 'edgeBefore').cursor, [0, 137]);
    assert.deepEqual(roleOf(report, id, 'edge').cursor, [0, 137]);
    assert.equal(roleOf(report, id, 'edge').viewport[0].trimStart().replace(/^0:\s*/, ''), 'Y');
    assert.deepEqual(roleOf(report, id, 'attr').viewport, ['0: AB   FG']);
    // Every graded and control role matched the independent model, on the main screen with full margins and no saved lines.
    for (const role of Object.values(byId(report, id).observed.roles)) {
        assert.equal(role.matchesModel, true);
        assert.equal(role.screen, 'main');
        assert.deepEqual(role.margins, [0, 39]);
        assert.equal(role.historyCount, 0);
    }
});

test('ECH: the other spellings are observations that match ECMA-48 and never affect the grade', () => {
    const { report } = load();
    const id = 'erase.character';
    for (const name of ['omitted', 'explicit0', 'explicit1']) assert.deepEqual(roleOf(report, id, name).viewport, ['0:  BCDE'], name);
    assert.deepEqual(roleOf(report, id, 'explicit5').viewport, []);
    assert.deepEqual(roleOf(report, id, 'wideOversized').viewport, ['0: ABOVE-ROW!', '1: AB', '2: BELOW-ROW!']);
    for (const name of ['omitted', 'explicit0', 'explicit1', 'explicit5', 'wideOversized']) assert.equal(roleOf(report, id, name).kind, 'observed', name);
    assert.deepEqual(byId(report, id).observed.spellingControls.names, ['omitted', 'explicit0', 'explicit1', 'explicit5', 'wideOversized']);
});

test('ECH: retained cells keep their stored attributes, erased cells carry no stale text attribute, and the stored background is a resolved literal', () => {
    const { report } = load();
    const attributes = byId(report, 'erase.character').observed.attributes;
    assert.equal(attributes.retainedKeepAttributes, true);
    assert.equal(attributes.erasedNoStaleAttributes, true);
    // Observed, not required: the erased cells carry the pen's resolved color, stored as a literal ARGB rather than the pen's palette index.
    assert.match(attributes.createdBlankBackground, /same blue|current background color, stored differently from the pen cell/);
    assert.deepEqual(attributes.createdBlankBackgroundDetail.pen, { bgIndex: 4, backgroundArgb: null, resolved: { r: 0, g: 0, b: 238 } });
    assert.deepEqual(attributes.createdBlankBackgroundDetail.firstErased, { bgIndex: -1, backgroundArgb: 0xff0000ee, resolved: { r: 0, g: 0, b: 238 } });
});

test('ED 3: nonempty ordered saved lines before, empty after; viewport, cursor, margins, modes and the main buffer preserved', () => {
    const { report } = load();
    const id = 'erase.screen.scrollback';
    assert.deepEqual(byId(report, id).observed.geometry, { rows: 40, cols: 140 });
    assert.equal(byId(report, id).observed.reverseVideo, false);
    // The pinned fixture: an observed empty baseline, three saved (blank) rows, then none.
    assert.equal(roleOf(report, id, 'initial').historyCount, 0);
    assert.equal(roleOf(report, id, 'before').historyCount, 3);
    assert.deepEqual(roleOf(report, id, 'before').history, [], 'blank rows have no content to list');
    assert.deepEqual(roleOf(report, id, 'before').cursor, [39, 0]);
    assert.equal(roleOf(report, id, 'after').historyCount, 0);
    assert.deepEqual(roleOf(report, id, 'after').cursor, [39, 0]);
    // The pinned total is the screen rows plus the saved physical rows.
    assert.deepEqual(['initial', 'before', 'after'].map(role => roleOf(report, id, role).totalLines), [40, 43, 40]);
    // The marked control: nine ordered rows, known by their source-written contents, are gone afterwards while everything else stays.
    const seed = roleOf(report, id, 'seed'), erased = roleOf(report, id, 'erase3');
    assert.deepEqual(seed.history, ['0-8: HIST-0..8']);
    assert.equal(seed.historyCount, 9);
    assert.deepEqual(seed.viewport, ['0-38: HIST-9..47']);
    assert.deepEqual(seed.cursor, [6, 11]);
    assert.deepEqual(erased.history, []);
    assert.equal(erased.historyCount, 0);
    assert.deepEqual(erased.viewport, seed.viewport);
    assert.deepEqual(erased.cursor, seed.cursor);
    assert.deepEqual([erased.screen, erased.margins], ['main', [0, 39]]);
    for (const role of Object.values(byId(report, id).observed.roles)) assert.equal(role.matchesModel, true);
});

test('ED 3 is told apart from ED 2, ordinary scrolling and an already-empty history', () => {
    const { report } = load();
    const id = 'erase.screen.scrollback';
    const ed2 = roleOf(report, id, 'ed2'), scrolled = roleOf(report, id, 'scrolled');
    assert.deepEqual(ed2.viewport, [], 'ED 2 clears the viewport');
    assert.deepEqual(ed2.history, ['0-8: HIST-0..8'], 'and keeps every saved line');
    assert.deepEqual(scrolled.history, ['0-11: HIST-0..11'], 'ordinary scrolling only grows the saved lines');
    assert.equal(scrolled.historyCount, 12);
    const empty = roleOf(report, id, 'emptyErase');
    assert.equal(roleOf(report, id, 'emptyBefore').historyCount, 0);
    assert.equal(empty.historyCount, 0);
    assert.deepEqual(empty.viewport, roleOf(report, id, 'emptyBefore').viewport, 'ED 3 over empty history changes nothing visible');
    const regrow = roleOf(report, id, 'regrow');
    assert.deepEqual(regrow.history, ['0-1: HIST-9..10'], 'after ED 3 only the newly scrolled rows are saved: the old ones are gone, not hidden');
    assert.equal(regrow.historyCount, 2);
    assert.match(byId(report, id).observed.units, /physical rows: no wrapped \(logical\) lines/);
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', async () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 33);
    assert.equal(receipt.eraseCharsHistoryEvidence.suiteId, eraseCharsHistorySuiteId);
    assert.equal(receipt.eraseCharsHistoryEvidence.runId, summary.context.runId);
    assert.deepEqual(receipt.eraseCharsHistoryEvidence.counts, { supported: 2 });
    assert.equal(receipt.eraseCharsHistoryEvidence.adapterBundleSha256, summary.context.eraseCharsHistoryBundleSha256);
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.eraseCharsHistoryEvidence.sha256, observationsHash);
    assert.equal(receipt.eraseCharsHistoryEvidence.runSha256, runHash);
    assert.equal(summary.retained.eraseCharsHistoryObservationsSha256, observationsHash);
    assert.equal(summary.retained.eraseCharsHistoryRunSha256, runHash);
    assert.equal(summary.retained.eraseCharsHistoryJournalSha256, journalHash);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v27');
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.ownedRunId, summary.context.runId);
    assert.match(summary.scriptsRevision, /^[0-9a-f]{40}$/);
    assert.match(summary.appRevision, /^[0-9a-f]{40}$/);
    // The predecessor inventory is derived from the merged contracts (every suite merged before this one), not assumed.
    const expected = [];
    for (const suite of benchmarkSuites.slice(0, benchmarkSuites.findIndex(item => item.suite === 'erase-chars-history'))) {
        const module = await import(new URL(suite.module, import.meta.url));
        expected.push(...module[suite.exports[2]]);
    }
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...new Set(expected)].sort());
    assert.equal(Object.keys(summary.sameRun).length, benchmarkSuites.findIndex(item => item.suite === 'erase-chars-history'), 'one same-run entry per merged predecessor suite');
    for (const [name, suite] of Object.entries(summary.sameRun)) {
        if (name === 'saved-reset') continue;
        assert.ok(suite.cases.every(item => item.outcome === 'supported'), name);
    }
    // modes.altscreen-47 and -1047 keep their preserved pinned-inconclusive verdicts beside their supported effect contracts.
    assert.deepEqual(summary.sameRun['saved-reset'].cases.map(item => [item.caseId, item.outcome, item.effectContract]), [
        ['modes.alt-screen.exit', 'supported', 'supported'], ['modes.altscreen-47', 'inconclusive', 'supported'], ['modes.altscreen-1047', 'inconclusive', 'supported'],
        ['reset.sgr', 'supported', 'supported'], ['reset.decaln', 'supported', 'supported']]);
    assert.ok(!eraseCharsHistoryCaseIds.some(id => unique.has(id)), 'the two new cases are counted separately');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock grades are carried, labeled as carried, and both ids are stock-inconclusive', () => {
    const { summary } = load();
    assert.equal(summary.stockCollector.rerun, false);
    assert.deepEqual(summary.stockCollector.carriedCounts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of eraseCharsHistoryCaseIds) assert.equal(summary.stockCollector.probes[id].outcome, 'inconclusive', id);
    const baseline = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-09-stock-baseline.json', import.meta.url)));
    for (const id of eraseCharsHistoryCaseIds) assert.equal(baseline.outcomes[id], 'inconclusive', id);
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of readdirSync(dir)) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('prior publications still rederive beside this batch, without this suite', () => {
    for (const [name, schema] of [['2026-11-08-erase-line', 25], ['2026-11-09-char-edit', 26], ['2026-11-09-erase-display', 27], ['2026-11-09-line-editing', 28],
        ['2026-11-09-charsets', 29], ['2026-11-09-scrollback', 30], ['2026-11-09-saved-reset', 31], ['2026-11-09-erase-attrs-region', 32]]) {
        const published = JSON.parse(readFileSync(new URL(`../../docs/qa/terminfo/${name}/summary.json`, import.meta.url)));
        assert.equal(published.receipt.schemaVersion, schema, name);
        assert.equal(published.receipt.eraseCharsHistoryEvidence, undefined, name);
    }
    const { attempts, context } = load().summary;
    assert.equal(attempts.length, 4);
    for (const attempt of attempts) assert.notEqual(attempt.runId, context.runId);
    // Three complete, superseded runs and one failed attempt that is not evidence.
    for (const attempt of attempts.slice(0, 3)) {
        assert.equal(attempt.receiptCompleted, true);
        assert.deepEqual(attempt.verdicts, { 'erase.character': 'supported', 'erase.screen.scrollback': 'supported' });
        assert.match(attempt.outcome, /superseded/);
    }
    assert.equal(attempts[3].receiptCompleted, false);
    assert.match(attempts[3].outcome, /failed, not evidence/);
});
