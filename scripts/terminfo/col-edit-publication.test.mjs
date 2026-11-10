// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked against the
// committed evidence. The publication is split into reviewable files (summary, run log, common frames with the report header, one
// file per case and one file per read frame); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildColEditGradingReport } from './col-edit-grading.mjs';
import { colEditAdapterId } from './col-edit-grading-collector.mjs';
import { colEditSuiteId, colEditContracts, colEditCaseIds } from './col-edit-contracts.mjs';
import { colEditCounts } from './col-edit-receipt.mjs';
import { benchmarkSuites } from './qualification-inventory.mjs';

const SL = 'editing.sl', SR = 'editing.sr', IC = 'editing.decic', DC = 'editing.decdc';
const dir = new URL('../../docs/qa/terminfo/2026-11-10-column-editing/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const load = () => {
    const summary = read('summary.json'), run = read('run-log.json'), common = read('journal-common.json'), header = common.reportHeader;
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of colEditContracts) {
        for (let index = 0; index < contract.roles.length; index++) {
            const journal = read(`journal-${contract.caseId}-read${index}.json`);
            assert.equal(journal.caseId, contract.caseId);
            assert.equal(journal.read, index);
            snapshots.push(journal.frame);
        }
    }
    snapshots.push(common.frames.finish);
    // The report header keeps the position of `cases` (null); the case files fill it in the contracted order.
    const cases = colEditCaseIds.map(id => read(`case-${id}.json`));
    return { summary, run, report: { ...header, cases, snapshots } };
};
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const observed = () => load().report.cases.map(item => item.observed);

test('the published evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildColEditGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, colEditSuiteId);
    assert.equal(report.adapter.id, colEditAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-rectangle-editing-grading-v1');
    assert.equal(report.snapshots.length, 3 + 13 + 13 + 14 + 14);
    assert.deepEqual(report.cases.map(item => item.caseId), [SL, SR, IC, DC]);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.pinned.observation.outcome]),
        [['supported', 'supported'], ['supported', 'supported'], ['supported', 'supported'], ['supported', 'supported']]);
    for (const item of report.cases) assert.equal(item.divergence, undefined, 'pinned and contract verdicts agree');
});

test('the pinned fixture bytes, pinned responses and recorded feeds are the exact upstream ones', () => {
    const { run, report } = load();
    const [sl, sr, ic, dc] = report.cases;
    assert.deepEqual(sl.pinnedFeeds, ['\x1b[1;1HABCDEFGHIJK\x1b[2;1HJKLMNOPQRST', '\x1b[1;1H', '\x1b[2 @']);
    assert.deepEqual(sr.pinnedFeeds, ['\x1b[1;1HABCDEFGHI\x1b[2;1HJKLMNOPQR', '\x1b[1;1H', '\x1b[2 A']);
    assert.deepEqual(ic.pinnedFeeds, ['\x1b[1;1HABCDEFGH\x1b[2;1HIJKLMNOP\x1b[3;1HQRSTUVWX', '\x1b[2;3H', "\x1b[2'}"]);
    assert.deepEqual(dc.pinnedFeeds, ['\x1b[1;1HABCDEFGHIJ\x1b[2;1HIJKLMNOPQR\x1b[3;1HQRSTUVWXab', '\x1b[2;3H', "\x1b[2'~"]);
    for (const [index, item] of report.cases.entries()) for (const feed of item.pinnedFeeds) assert.ok(run.cases[index].feeds.includes(feed), JSON.stringify(feed));
    const chars = text => [...text];
    assert.equal(sl.pinned.response, JSON.stringify({ before: ['ABCDEFGHIJK', 'JKLMNOPQRST'].map(chars), setup: { x: 0, y: 0, visible: true },
        after: ['CDEFGHIJK', 'LMNOPQRST'].map(chars) }));
    assert.equal(sr.pinned.response, JSON.stringify({ before: ['ABCDEFGHI', 'JKLMNOPQR'].map(chars), setup: { x: 0, y: 0, visible: true },
        after: ['  ABCDEFG', '  JKLMNOP'].map(chars) }));
    assert.equal(ic.pinned.response, JSON.stringify({ before: ['ABCDEFGH', 'IJKLMNOP', 'QRSTUVWX'].map(chars), setup: { x: 2, y: 1, visible: true },
        after: ['AB  CDEF', 'IJ  KLMN', 'QR  STUV'].map(chars) }));
    assert.equal(dc.pinned.response, JSON.stringify({ before: ['ABCDEFGHIJ', 'IJKLMNOPQR', 'QRSTUVWXab'].map(chars), setup: { x: 2, y: 1, visible: true },
        after: ['ABEFGHIJ', 'IJMNOPQR', 'QRUVWXab'].map(chars) }));
});

test('every control of every case matched the independent column model over the whole 40x140 screen, with the cursor and state as contracted', () => {
    for (const [index, record] of observed().entries()) {
        const contract = colEditContracts[index];
        assert.deepEqual(record.geometry, { rows: 40, cols: 140 });
        assert.equal(record.reverseVideo, false);
        assert.equal(record.initial.ink, 0);
        assert.equal(record.blockSeed.ink, 140, 'ten characters at each edge of seven rows');
        for (const role of contract.others) {
            const control = record.controls[role];
            assert.equal(control.matchesModel, true, `${contract.caseId} ${role}`);
            assert.equal(control.insert, role === 'modes');
            assert.equal(control.origin, role === 'origin');
            assert.deepEqual(control.margins, ['origin', 'region'].includes(role) ? [2, 4] : [0, 39], `${contract.caseId} ${role}`);
            assert.equal(control.scrollback, 0);
        }
        assert.deepEqual(record.edited.cursor, record.setup.cursor, 'the cursor did not move');
        assert.equal(record.attributes.referenceMeasured, true);
        assert.equal(record.attributes.penMeasured, true);
        assert.equal(record.attributes.changedOk, true);
        assert.equal(record.attributes.cursorOk, true);
        assert.deepEqual(record.history.scrollbackLines, [0]);
    }
});

test('SL and SR: the pinned shifts, the right edge, clamping, the region and its outside, origin and insert mode', () => {
    const [sl, sr] = observed();
    assert.deepEqual(sl.edited.text.slice(0, 2), ['CDEFGHIJK_______', 'LMNOPQRST_______']);
    assert.deepEqual(sr.edited.text.slice(0, 2), ['__ABCDEFGHI_____', '__JKLMNOPQR_____']);
    assert.deepEqual([sl.before.cursor, sl.setup.cursor, sl.edited.cursor], [[1, 11], [0, 0], [0, 0]]);
    // Counts 1, omitted, zero and 3 compose to a shift of six from the left margin, whatever the cursor column (6).
    assert.deepEqual(sl.controls.counts.text.slice(0, 2), ['GHIJ____________', 'QRST____________']);
    assert.deepEqual(sl.controls.counts.rightEdge.slice(0, 2), ['456789______', '543210______'], 'blank columns entered at the right edge');
    assert.deepEqual(sr.controls.counts.rightEdge.slice(0, 2), ['________0123', '________9876'], 'columns pushed past the right edge were lost');
    for (const record of [sl, sr]) {
        assert.equal(record.controls.clip.ink, 0, 'an oversized count cleared every row of the page');
        assert.equal(record.controls.region.ink, 134, 'only the three region rows moved; the cursor above and below the margins changed nothing');
        assert.equal(record.controls.origin.ink, 134);
        assert.equal(record.controls.modes.ink, 126, 'the three non-admitted spellings changed nothing; insert mode did not change the admitted shift');
    }
});

test('DECIC and DECDC: the pinned edits at the cursor column, clamping, the first and last column', () => {
    const [, , ic, dc] = observed();
    assert.deepEqual(ic.edited.text.slice(0, 3), ['AB__CDEFGH______', 'IJ__KLMNOP______', 'QR__STUVWX______']);
    assert.deepEqual(dc.edited.text.slice(0, 3), ['ABEFGHIJ________', 'IJMNOPQR________', 'QRUVWXab________']);
    assert.deepEqual([ic.before.cursor, ic.setup.cursor, ic.edited.cursor], [[2, 8], [1, 2], [1, 2]]);
    assert.deepEqual(ic.controls.counts.text.slice(0, 2), ['ABCDE______FGHIJ', 'KLMNO______PQRST'], 'six blank columns inserted at column 6');
    assert.deepEqual(dc.controls.counts.text.slice(0, 2), ['ABCDE___________', 'KLMNO___________']);
    for (const record of [ic, dc]) assert.deepEqual(record.controls.clip.text.slice(0, 1), ['ABCDE___________'], 'an oversized count cleared from the cursor column');
    assert.deepEqual(ic.controls.columns.rightEdge.slice(0, 1), ['____0123456_'], 'at the last column one column was blanked');
    assert.deepEqual(ic.controls.columns.cursor, [3, 139]);
    assert.deepEqual(dc.controls.columns.text.slice(0, 1), ['CDEFGHIJ________']);
});

test('attributes and protection: moved cells kept their stored tuples and DECSCA protection, introduced blanks carried none', () => {
    const [sl, sr, ic, dc] = observed();
    assert.deepEqual(sl.attributes.after.protectedCells, [[0, 135], [0, 136], [0, 137], [3, 4], [3, 5], [3, 6]], 'XYZ and ROW moved left by two with their protection');
    assert.deepEqual(sr.attributes.after.protectedCells, [[0, 139], [3, 8], [3, 9], [3, 10]], 'Y and Z were pushed past the edge; X and ROW kept their protection');
    assert.deepEqual(ic.attributes.after.protectedCells, [[0, 139], [3, 8], [3, 9], [3, 10]]);
    assert.deepEqual(dc.attributes.after.protectedCells, [[0, 135], [0, 136], [0, 137], [3, 4], [3, 5], [3, 6]]);
    assert.deepEqual(dc.attributes.after.text.slice(0, 4), ['KE-TOP--________', 'BO-ROW--________', 'REON-BLU________', 'PLN-ROW-________']);
    for (const record of [sl, sr, ic, dc]) {
        assert.match(record.attributes.policy, /colors of introduced blanks are observed, not required/);
        assert.equal(record.attributes.blankFill, 'introduced blanks carry the current colors', 'observed: Ntilde fills like ICH/DCH');
    }
});

test('the pending-wrap state is observed, never graded', () => {
    for (const [index, record] of observed().entries()) {
        assert.match(record.pendingWrap.policy, /observed, not graded/);
        assert.equal(record.pendingWrap.reading, 'kept: the next character wrapped to the next row');
        assert.ok(!colEditContracts[index].others.includes('pendingWrap'));
    }
    const source = readFileSync(new URL('./col-edit-contracts.mjs', import.meta.url), 'utf8');
    assert.match(source, /notControlRoles = new Set\(\[[^\]]*'pendingWrap'\]\)/);
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', async () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 38);
    assert.equal(receipt.colEditEvidence.suiteId, colEditSuiteId);
    assert.equal(receipt.colEditEvidence.runId, summary.context.runId);
    const counts = colEditCounts(report);
    assert.deepEqual(counts, { supported: 4 });
    assert.deepEqual(receipt.colEditEvidence.counts, counts);
    assert.equal(receipt.colEditEvidence.adapterBundleSha256, summary.context.colEditBundleSha256);
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.colEditEvidence.sha256, observationsHash);
    assert.equal(receipt.colEditEvidence.runSha256, runHash);
    assert.equal(summary.retained.colEditObservationsSha256, observationsHash);
    assert.equal(summary.retained.colEditRunSha256, runHash);
    assert.equal(summary.retained.colEditJournalSha256, journalHash);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v32');
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.ownedRunId, summary.context.runId);
    assert.notEqual(summary.stockCollectorRunId, summary.ownedRunId);
    // The predecessor inventory is derived from the merged contracts, not assumed: every earlier benchmark suite's case ids.
    const predecessors = benchmarkSuites.slice(0, benchmarkSuites.findIndex(item => item.suite === 'col-edit'));
    const expected = [];
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
    assert.ok(![SL, SR, IC, DC].some(id => unique.has(id)), 'the four new cases are counted separately');
    assert.ok(summary.sameRun['rect-edit'].cases.every(item => item.outcome === 'supported'));
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock run keeps its own grades: all four pinned cases are inconclusive there (cursor-query path)', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of [SL, SR, IC, DC]) {
        assert.deepEqual([summary.stockCollector.probes[id].outcome, summary.stockCollector.probes[id].evidence], ['inconclusive', 'query'], id);
    }
});

test('the App revision and the adapter (scripts) revision are recorded separately', () => {
    const { summary } = load();
    assert.match(summary.appRevision, /^[0-9a-f]{40}$/);
    assert.match(summary.scriptsRevision, /^[0-9a-f]{40}$/);
    assert.notEqual(summary.appRevision, summary.scriptsRevision);
    assert.equal(summary.context.appVersion, '0.13.0');
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of readdirSync(dir)) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('prior publications still rederive beside this batch and every attempt is preserved', () => {
    const earlier = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-09-rectangle-editing/summary.json', import.meta.url)));
    assert.equal(earlier.receipt.schemaVersion, 37);
    assert.equal(earlier.receipt.colEditEvidence, undefined);
    const { attempts, context } = load().summary;
    assert.equal(attempts.length, 3);
    assert.equal(attempts[0].receiptCompleted, false);
    assert.match(attempts[0].outcome, /Oversized owner response/);
    assert.equal(attempts.at(-1).runId, context.runId);
    assert.equal(attempts.at(-1).receiptCompleted, true);
    assert.ok(attempts.every(attempt => /^[0-9a-f]{32}$/.test(attempt.runId)));
});
