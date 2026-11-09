// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked against the
// committed evidence. The publication is split into reviewable files (summary, run log, common frames with the report header, one
// file per case and one file per read frame); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildRectEditGradingReport } from './rect-edit-grading.mjs';
import { rectEditAdapterId } from './rect-edit-grading-collector.mjs';
import { rectEditSuiteId, rectEditContracts, rectEditCaseIds } from './rect-edit-contracts.mjs';
import { rectEditCounts } from './rect-edit-receipt.mjs';
import { benchmarkSuites } from './qualification-inventory.mjs';

const FRA = 'editing.decfra', ERA = 'editing.decera', CRA = 'editing.deccra';
const dir = new URL('../../docs/qa/terminfo/2026-11-09-rectangle-editing/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const load = () => {
    const summary = read('summary.json'), run = read('run-log.json'), common = read('journal-common.json'), header = common.reportHeader;
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of rectEditContracts) {
        for (let index = 0; index < contract.roles.length; index++) {
            const journal = read(`journal-${contract.caseId}-read${index}.json`);
            assert.equal(journal.caseId, contract.caseId);
            assert.equal(journal.read, index);
            snapshots.push(journal.frame);
        }
    }
    snapshots.push(common.frames.finish);
    // The report header keeps the position of `cases` (null); the case files fill it in the contracted order.
    const cases = rectEditCaseIds.map(id => read(`case-${id}.json`));
    return { summary, run, report: { ...header, cases, snapshots } };
};
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const observed = () => load().report.cases.map(item => item.observed);

test('the published evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildRectEditGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, rectEditSuiteId);
    assert.equal(report.adapter.id, rectEditAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-erase-chars-history-grading-v1');
    assert.equal(report.snapshots.length, 3 + 17 + 16 + 20);
    assert.deepEqual(report.cases.map(item => item.caseId), [FRA, ERA, CRA]);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.pinned.observation.outcome]), [['supported', 'supported'], ['supported', 'supported'], ['supported', 'supported']]);
    for (const item of report.cases) assert.equal(item.divergence, undefined, 'pinned and contract verdicts agree');
});

test('the pinned fixture bytes, pinned responses and recorded feeds are the exact upstream ones', () => {
    const { run, report } = load();
    const [fra, era, cra] = report.cases;
    assert.deepEqual(fra.pinnedFeeds, ['\x1b[1;1HaaaaaZ\x1b[2;1HbbbbbY\x1b[3;1HcccccW', '\x1b[1;1H', '\x1b[88;1;1;3;5$x']);
    assert.deepEqual(era.pinnedFeeds, ['\x1b[1;1HaaaaaZ\x1b[2;1HbbbbbY\x1b[3;1HcccccX', '\x1b[1;1H', '\x1b[1;1;3;5$z']);
    assert.deepEqual(cra.pinnedFeeds, ['\x1b[1;1HABCDE.........\x1b[2;1HFGHIJ.........\x1b[3;1H..............\x1b[4;1H..............\x1b[5;1H.........12345\x1b[6;1H.........67890',
        '\x1b[1;1H', '\x1b[1;1;2;5;1;5;10$v']);
    for (const [index, item] of report.cases.entries()) for (const feed of item.pinnedFeeds) assert.ok(run.cases[index].feeds.includes(feed), JSON.stringify(feed));
    const cursor = { x: 0, y: 0, visible: true };
    assert.equal(fra.pinned.response, JSON.stringify({ before: ['aaaaaZ', 'bbbbbY', 'cccccW'], setup: cursor, after: ['XXXXXZ', 'XXXXXY', 'XXXXXW'] }));
    assert.equal(era.pinned.response, JSON.stringify({ before: [[...'aaaaaZ'], [...'bbbbbY'], [...'cccccX']], setup: cursor,
        after: [[' ', ' ', ' ', ' ', ' ', 'Z'], [' ', ' ', ' ', ' ', ' ', 'Y'], [' ', ' ', ' ', ' ', ' ', 'X']] }));
    assert.equal(cra.pinned.response, JSON.stringify({ before: ['ABCDE.........', 'FGHIJ.........', '..............', '..............', '.........12345', '.........67890'], setup: cursor,
        after: ['ABCDE.........', 'FGHIJ.........', '..............', '..............', '.........ABCDE', '.........FGHIJ'] }));
});

test('every control of every case matched the independent rectangle model over the whole 40x140 screen, with the cursor and state as contracted', () => {
    for (const [index, record] of observed().entries()) {
        const contract = rectEditContracts[index];
        assert.deepEqual(record.geometry, { rows: 40, cols: 140 });
        assert.equal(record.reverseVideo, false);
        assert.equal(record.initial.ink, 0);
        for (const role of contract.others) {
            const control = record.controls[role];
            assert.equal(control.matchesModel, true, `${contract.caseId} ${role}`);
            assert.equal(control.insert, role === 'insertMode');
            assert.equal(control.origin, role === 'origin');
            assert.deepEqual(control.margins, ['origin', 'margins'].includes(role) ? [2, 4] : [0, 39], `${contract.caseId} ${role}`);
            assert.equal(control.scrollback, 0);
        }
        assert.deepEqual(record.edited.cursor, [0, 0], 'the cursor did not move');
        assert.equal(record.attributes.referenceMeasured, true);
        assert.equal(record.attributes.penMeasured, true);
        assert.equal(record.attributes.changedOk, true);
        assert.equal(record.attributes.cursorOk, true);
        assert.deepEqual(record.history.scrollbackLines, [0]);
    }
});

test('editing.decfra: the 3x5 pinned fill, then bounds, defaults, clipping, no-ops, margins, insert mode and both pens', () => {
    const [fra] = observed();
    assert.equal(fra.pinnedSequence, '\x1b[88;1;1;3;5$x');
    assert.deepEqual(fra.before.text.slice(0, 3), ['aaaaaZ__________', 'bbbbbY__________', 'cccccW__________']);
    assert.deepEqual([fra.before.cursor, fra.setup.cursor, fra.edited.cursor], [[2, 6], [0, 0], [0, 0]]);
    assert.deepEqual(fra.edited.text.slice(0, 3), ['XXXXXZ__________', 'XXXXXY__________', 'XXXXXW__________']);
    // A one-page fill is complete: 70 block-seed cells in every unaffected control, the full 40x140 page filled by the defaults.
    assert.equal(fra.controls.defaults.ink, 40 * 140);
    assert.equal(fra.controls.zero.ink, 40 * 140);
    assert.equal(fra.controls.clip.lastColumn, '__+', 'the oversized borders clipped to the page edge');
    assert.equal(fra.controls.noop.ink, 70, 'invalid codes, reversed borders and an extra parameter changed nothing');
    assert.equal(fra.controls.insertMode.matchesModel, true, 'a fill writes cells; insert mode does not shift them');
    assert.match(fra.attributes.policy, /stored tuple of a reference cell/);
    assert.equal(fra.literalPen.filledOk, true);
    assert.equal(fra.literalPen.restOk, true);
});

test('editing.decera: the 3x5 pinned erase, blank cells with only a background, surrounding cells intact, ordinary erase unconditional', () => {
    const [, era] = observed();
    assert.equal(era.pinnedSequence, '\x1b[1;1;3;5$z');
    assert.deepEqual(era.edited.text.slice(0, 3), ['_____Z__________', '_____Y__________', '_____X__________']);
    assert.equal(era.edited.ink, 3);
    assert.equal(era.controls.defaults.ink, 0, 'the omitted-border erase blanked the whole page');
    assert.equal(era.controls.zero.ink, 0);
    assert.equal(era.controls.noop.ink, 70);
    assert.match(era.attributes.policy, /background fill is observed, not required/);
    assert.equal(era.attributes.backgroundFill, 'erased cells carry the current background color');
    assert.deepEqual(era.protectedText.text.slice(1, 4), ['KL____QRST______', 'UV____abcd______', 'ef____klmn______'], 'DECERA ignores character protection: it is the unconditional erase');
    assert.match(era.protectedText.policy, /DECSERA is not claimed/);
});

test('editing.deccra: the 2x5 pinned copy, then overlap in every direction without smearing, page edges, clipping and attributes', () => {
    const [, , cra] = observed();
    assert.equal(cra.pinnedSequence, '\x1b[1;1;2;5;1;5;10$v');
    assert.deepEqual(cra.edited.text.slice(0, 6), ['ABCDE.........__', 'FGHIJ.........__', '..............__', '..............__', '.........ABCDE__', '.........FGHIJ__']);
    assert.deepEqual(cra.before.text.slice(4, 6), ['.........12345__', '.........67890__']);
    for (const role of ['right', 'left', 'down', 'up', 'diagonal']) assert.equal(cra.controls[role].matchesModel, true, `${role} overlap was read before it was written`);
    assert.equal(cra.controls.self.ink, 70, 'a copy of the page onto itself changes nothing');
    assert.equal(cra.controls.cornerRow.lastRow, 'ABC_____________', 'a destination row clamped to the last row clipped the second source row');
    assert.equal(cra.controls.cornerCol.lastColumn, 'AK_', 'a destination column clamped to the last column clipped all but the first source column');
    assert.equal(cra.controls.noop.ink, 70);
});

test('unsupported DECCRA pages are an Ntilde policy: they were observed to be ignored, and were never graded', () => {
    const [, , cra] = observed();
    assert.match(cra.pages.policy, /observed, not graded/);
    assert.match(cra.pages.policy, /not a pinned or DEC criterion/);
    assert.deepEqual(cra.pages.sequences, ['\x1b[1;1;2;5;2;5;6$v', '\x1b[1;1;2;5;1;5;6;2$v']);
    assert.equal(cra.pages.ignored, true, 'neither page 2 source nor page 2 destination was aliased to the active page');
    assert.equal(rectEditContracts[2].roles.includes('pages'), true);
    const source = readFileSync(new URL('./rect-edit-contracts.mjs', import.meta.url), 'utf8');
    assert.match(source, /notControlRoles = \[[^\]]*'pages'\]/);
    assert.match(source, /!notControlRoles\.includes\(item\)/);
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', async () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 36);
    assert.equal(receipt.rectEditEvidence.suiteId, rectEditSuiteId);
    assert.equal(receipt.rectEditEvidence.runId, summary.context.runId);
    const counts = rectEditCounts(report);
    assert.deepEqual(counts, { supported: 3 });
    assert.deepEqual(receipt.rectEditEvidence.counts, counts);
    assert.equal(receipt.rectEditEvidence.adapterBundleSha256, summary.context.rectEditBundleSha256);
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.rectEditEvidence.sha256, observationsHash);
    assert.equal(receipt.rectEditEvidence.runSha256, runHash);
    assert.equal(summary.retained.rectEditObservationsSha256, observationsHash);
    assert.equal(summary.retained.rectEditRunSha256, runHash);
    assert.equal(summary.retained.rectEditJournalSha256, journalHash);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v30');
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.ownedRunId, summary.context.runId);
    assert.notEqual(summary.stockCollectorRunId, summary.ownedRunId);
    // The predecessor inventory is derived from the merged contracts, not assumed: every other benchmark suite's case ids.
    const expected = [];
    // Only the suites merged before this one, in merge order.
    const predecessors = benchmarkSuites.slice(0, benchmarkSuites.findIndex(item => item.suite === 'rect-edit'));
    for (const suite of predecessors) {
        const module = await import(new URL(suite.module, import.meta.url));
        expected.push(...module[suite.exports[2]]);
    }
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...new Set(expected)].sort());
    assert.equal(Object.keys(summary.sameRun).length, predecessors.length, 'one same-run entry per merged predecessor suite');
    for (const [name, suite] of Object.entries(summary.sameRun)) {
        // Suites with pinned-inconclusive cases keep them; their separately identified effect contracts are all supported.
        if (suite.cases.some(item => item.effectContract)) assert.ok(suite.cases.every(item => item.effectContract === 'supported'), name);
        else assert.ok(suite.cases.every(item => item.outcome === 'supported'), name);
    }
    assert.ok(![FRA, ERA, CRA].some(id => unique.has(id)), 'the three new cases are counted separately');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock run keeps its own grades: all three pinned cases are inconclusive there (cursor-query path)', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of [FRA, ERA, CRA]) {
        assert.deepEqual([summary.stockCollector.probes[id].outcome, summary.stockCollector.probes[id].evidence], ['inconclusive', 'query'], id);
    }
});

test('the App revision and the adapter (scripts) revision are recorded separately', () => {
    const { summary } = load();
    assert.match(summary.appRevision, /^[0-9a-f]{40}$/);
    assert.match(summary.scriptsRevision, /^[0-9a-f]{40}$/);
    assert.equal(summary.context.appVersion, '0.13.0');
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of readdirSync(dir)) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('prior publications still rederive beside this batch and every attempt is preserved', () => {
    const earlier = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-09-erase-chars-history/summary.json', import.meta.url)));
    assert.equal(earlier.receipt.schemaVersion, 35);
    assert.equal(earlier.receipt.rectEditEvidence, undefined);
    const { attempts, context } = load().summary;
    assert.ok(attempts.length >= 1);
    assert.equal(attempts.at(-1).runId, context.runId);
    assert.equal(attempts.at(-1).receiptCompleted, true);
    assert.ok(attempts.every(attempt => /^[0-9a-f]{32}$/.test(attempt.runId)));
});
