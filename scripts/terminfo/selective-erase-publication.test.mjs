// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked against the
// committed evidence. The publication is split into reviewable files (summary, report header and per-case reports, run-log header and per-case runs,
// common frames and one file per read frame); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildSelectiveEraseGradingReport } from './selective-erase-grading.mjs';
import { selectiveEraseAdapterId } from './selective-erase-grading-collector.mjs';
import { selectiveEraseSuiteId, selectiveEraseContracts, pinnedSeed, pinnedFinalizer } from './selective-erase-contracts.mjs';
import { benchmarkSuites } from './qualification-inventory.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-09-selective-erase/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const ids = ['erase.selective', 'editing.decsera'];
const load = () => {
    const summary = read('summary.json'), header = read('report.json'), runHeader = read('run-log.json'), common = read('journal-common.json');
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of selectiveEraseContracts) {
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
    return { summary, run, report };
};
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const observed = () => load().report.cases.map(item => item.observed);

test('the published selective-erasure evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildSelectiveEraseGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, selectiveEraseSuiteId);
    assert.equal(report.adapter.id, selectiveEraseAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-erase-chars-history-grading-v1');
    assert.equal(report.snapshots.length, 3 + 20 + 21);
    assert.deepEqual(report.cases.map(item => item.caseId), ids);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.pinned.observation.outcome]), ids.map(() => ['supported', 'supported']));
    for (const item of report.cases) assert.equal(item.divergence, undefined);
});

test('the pinned bytes and results are exactly the pinned fixture, erase and finalizer', () => {
    const { run, report } = load();
    assert.equal(pinnedSeed, '\x1b[1;1H\x1b[1"qP\x1b[0"qABCDZ');
    for (const [index, item] of report.cases.entries()) {
        assert.deepEqual(item.pinnedFeeds, [pinnedSeed, ['\x1b[?2J', '\x1b[1;1;1;5${'][index], pinnedFinalizer]);
        assert.deepEqual(run.cases[index].pinnedFed, item.pinnedFeeds);
        assert.equal(item.pinned.assertions[0].expected, 'DECSCA-protected P survives while selective erase clears ABCD');
        assert.equal(item.pinned.note, null);
        assert.ok(run.cases[index].feeds.includes(pinnedSeed));
    }
    assert.equal(report.cases[0].pinned.response, JSON.stringify({ before: ['P', 'A', 'B', 'C', 'D', 'Z'], after: ['P', ' ', ' ', ' ', ' ', ' '] }));
    assert.equal(report.cases[1].pinned.response, JSON.stringify({ before: ['P', 'A', 'B', 'C', 'D', 'Z'], after: ['P', ' ', ' ', ' ', ' ', 'Z'] }));
});

test('the seed read back with stored protection on P only, and each erase left exactly the protected cell (and, for DECSERA, the outside Z)', () => {
    const [sed, sera] = observed();
    for (const item of [sed, sera]) {
        assert.deepEqual(item.geometry, { rows: 40, cols: 140 });
        assert.equal(item.reverseVideo, false);
        assert.equal(item.initial.ink, 0);
        assert.equal(item.seeded.text.slice(0, 6), 'PABCDZ');
        assert.deepEqual(item.seeded.protectedColumns, [0], 'only P was stored protected');
        assert.deepEqual(item.seeded.cursor, [0, 6]);
        assert.deepEqual(item.erased.protectedColumns, [0]);
        assert.deepEqual(item.erased.cursor, [0, 6]);
        assert.equal(item.modesUnchanged, true);
        assert.deepEqual(item.finalized.protectedColumns, [0]);
    }
    assert.equal(sed.erased.text.slice(0, 6), 'P_____');
    assert.equal(sera.erased.text.slice(0, 6), 'P____Z');
});

test('an ordinary erase of the same seed removes the protected P: protection is not read-only text', () => {
    const [sed, sera] = observed();
    assert.equal(sed.ordinaryControl.name, 'ED 2');
    assert.equal(sed.ordinaryControl.after.text, '____________');
    assert.deepEqual(sed.ordinaryControl.after.protectedColumns, []);
    assert.equal(sera.ordinaryControl.name, 'DECERA 1;1;1;5');
    assert.equal(sera.ordinaryControl.after.text.slice(0, 6), '_____Z');
    assert.deepEqual(sera.ordinaryControl.after.protectedColumns, []);
});

test('the bounds and mode controls: DECSERA interior, wide and reversed rectangles; DECSED modes 0 and 1 observed', () => {
    const [sed, sera] = observed();
    assert.equal(sera.variants.rectInterior.text.slice(0, 6), 'P___DZ');
    assert.equal(sera.variants.rectWide.text.slice(0, 6), 'P_____');
    assert.equal(sera.variants.rectReversed.text.slice(0, 6), 'PABCDZ');
    assert.match(sera.variants.policy, /required bounds controls/);
    assert.equal(sed.variants.mode0.text.slice(0, 6), 'PAB___');
    assert.equal(sed.variants.mode1.text.slice(0, 6), 'P___DZ');
    assert.match(sed.variants.policy, /observed, not graded/);
});

test('turning protection off affects only later writes, an overwrite takes the pen, and the pinned finalizer restores it', () => {
    for (const item of observed()) {
        assert.deepEqual(item.protectionOff.before.protectedColumns, [0, 1]);
        assert.equal(item.protectionOff.before.text.slice(0, 4), 'ABCD');
        assert.deepEqual(item.protectionOff.after.protectedColumns, [0, 1]);
        assert.equal(item.protectionOff.after.text.slice(0, 4), 'AB__');
        assert.deepEqual(item.overwrite.before.protectedColumns, [1]);
        assert.equal(item.overwrite.before.text.slice(0, 2), 'XB');
        assert.equal(item.overwrite.after.text.slice(0, 2), '_B');
        assert.deepEqual(item.restoration.after.protectedColumns, []);
        assert.equal(item.restoration.after.text.slice(0, 1), 'Q');
    }
});

test('the full-screen fill and edge controls keep every protected cell, erase only unprotected ones and never move the cursor', () => {
    const [sed, sera] = observed();
    for (const item of [sed, sera]) {
        assert.deepEqual([item.fullScreen.before.inkCells, item.fullScreen.before.protectedCells], [440, 27]);
        assert.deepEqual(item.fullScreen.before.cursor, [20, 4]);
        assert.deepEqual(item.fullScreen.after.cursor, [20, 4]);
        assert.equal(item.fullScreen.after.protectedCells, 27);
        assert.deepEqual(item.edge.after.cursor, [39, 139]);
        assert.deepEqual([item.edge.after.inkCells, item.edge.after.protectedCells], [27, 27]);
    }
    assert.deepEqual([sed.fullScreen.after.inkCells, sed.fullScreen.after.unprotectedCells], [27, 0]);
    assert.ok(sera.fullScreen.after.unprotectedCells > 0 && sera.fullScreen.after.unprotectedCells < 413, 'the rectangle erased some but not all unprotected cells');
});

test('retained protected cells keep bold; background-color erase is observed (not required) and described, never graded', () => {
    for (const item of observed()) {
        const { attributes } = item;
        assert.equal(attributes.retainedKeepBold, true);
        assert.match(attributes.policy, /observed, not required/);
        assert.equal(attributes.backgroundColorErase, 'erased cells carry the current background');
        assert.equal(attributes.backgroundColorEraseDetail.erasedCells, attributes.backgroundColorEraseDetail.paintedCells);
        assert.deepEqual(attributes.backgroundColorEraseDetail.firstPainted.resolved, { r: 0, g: 0, b: 238 });
    }
});

test('viewport erasure never removes saved lines on the main screen', () => {
    for (const item of observed()) {
        const { history } = item;
        assert.equal(history.seeded, true);
        assert.equal(history.linesKept, true);
        assert.equal(history.linesAdded, 0);
        assert.equal(history.before.scrollback.alternateScreen, false);
        assert.deepEqual(history.before.scrollback.history, ['HIST-A', 'HIST-B', 'HIST-C', '']);
        assert.deepEqual(history.after.scrollback.history, history.before.scrollback.history);
        assert.deepEqual(history.after.protectedColumns, [0]);
        assert.deepEqual([item.seeded.scrollback.alternateScreen, item.seeded.scrollback.scrollbackLines], [true, 0]);
    }
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', async () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 36);
    assert.equal(receipt.selectiveEraseEvidence.suiteId, selectiveEraseSuiteId);
    assert.equal(receipt.selectiveEraseEvidence.runId, summary.context.runId);
    assert.deepEqual(receipt.selectiveEraseEvidence.counts, { supported: 2 });
    assert.equal(receipt.selectiveEraseEvidence.adapterBundleSha256, summary.context.selectiveEraseBundleSha256);
    assert.deepEqual(receipt.eraseCharsHistoryEvidence.counts, { supported: 2 });
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.selectiveEraseEvidence.sha256, observationsHash);
    assert.equal(receipt.selectiveEraseEvidence.runSha256, runHash);
    assert.equal(summary.retained.selectiveEraseObservationsSha256, observationsHash);
    assert.equal(summary.retained.selectiveEraseRunSha256, runHash);
    assert.equal(summary.retained.selectiveEraseJournalSha256, journalHash);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v30');
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.ownedRunId, summary.context.runId);
    assert.equal(summary.stockCollectorRunId, undefined, 'no stock collector run belongs to this batch');
    // The predecessor inventory is derived from the merged contracts (every suite merged before this one), not assumed.
    const expected = [];
    for (const suite of benchmarkSuites.slice(0, benchmarkSuites.findIndex(item => item.suite === 'selective-erase'))) {
        const module = await import(new URL(suite.module, import.meta.url));
        expected.push(...module[suite.exports[2]]);
    }
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...new Set(expected)].sort());
    assert.equal(Object.keys(summary.sameRun).length, benchmarkSuites.findIndex(item => item.suite === 'selective-erase'), 'one same-run entry per merged predecessor suite');
    // Suites whose pinned verdicts are inconclusive by design keep them beside separately identified, supported effect contracts.
    const byDesign = new Set(['saved-reset', 'attr-rect']);
    for (const [name, suite] of Object.entries(summary.sameRun)) {
        if (byDesign.has(name)) assert.ok(suite.cases.every(item => item.effectContract === 'supported'), name);
        else assert.ok(suite.cases.every(item => item.outcome === 'supported'), name);
    }
    assert.ok(!ids.some(id => unique.has(id)), 'the two new cases are counted separately');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock run keeps its own grades: both pinned cases are inconclusive there (the pinned paths only query the cursor)', () => {
    const { summary } = load();
    assert.equal(summary.stockCollector.rerun, false);
    assert.deepEqual(summary.stockCollector.carriedCounts, { inconclusive: 168, supported: 86, unsupported: 1 });
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

test('prior publications still rederive beside this batch and every failed and superseded attempt is preserved', () => {
    for (const [name, schema] of [['2026-11-09-saved-reset', 31], ['2026-11-09-erase-attrs-region', 32], ['2026-11-09-attr-rect', 33], ['2026-11-09-cursor', 34], ['2026-11-09-erase-chars-history', 35]]) {
        const published = JSON.parse(readFileSync(new URL(`../../docs/qa/terminfo/${name}/summary.json`, import.meta.url)));
        assert.equal(published.receipt.schemaVersion, schema, name);
        assert.equal(published.receipt.selectiveEraseEvidence, undefined, name);
    }
    const { attempts, context } = load().summary;
    assert.ok(attempts.length >= 2);
    for (const attempt of attempts) assert.notEqual(attempt.runId, context.runId);
    assert.equal(attempts[0].receiptCompleted, false);
    assert.ok(attempts.some(attempt => /Oversized owner response/.test(attempt.outcome)), 'the failed oversized-response trial is preserved');
    assert.ok(attempts.some(attempt => /contract-stricter-than-pinned/.test(attempt.outcome)), 'the superseded contract-defect trial is preserved');
});
