// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked against the committed
// evidence. The publication is split into reviewable files (summary, report header, one file per case, run log, common frames and one file
// per read frame); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildAttrRectGradingReport } from './attr-rect-grading.mjs';
import { attrRectAdapterId } from './attr-rect-grading-collector.mjs';
import { attrRectSuiteId, attrRectContracts, attrRectEffectContractId } from './attr-rect-contracts.mjs';
import { attrRectCounts } from './attr-rect-receipt.mjs';
import { benchmarkSuites } from './qualification-inventory.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-09-attr-rect/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const CCARA = 'editing.deccara', RARA = 'editing.decrara', SACE = 'editing.decsace';
const ids = [CCARA, RARA, SACE];
const load = () => {
    const summary = read('summary.json'), header = read('report.json'), run = read('run-log.json'), common = read('journal-common.json');
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of attrRectContracts) {
        for (let index = 0; index < contract.roles.length; index++) {
            const journal = read(`journal-${contract.caseId}-read${index}.json`);
            assert.equal(journal.caseId, contract.caseId);
            assert.equal(journal.read, index);
            snapshots.push(journal.frame);
        }
    }
    snapshots.push(common.frames.finish);
    const cases = header.cases.map(caseId => read(`case-${caseId}.json`));
    return { summary, run, report: { ...header, cases, snapshots } };
};
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const byId = (report, id) => report.cases.find(item => item.caseId === id);

test('the published attribute-rectangle evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildAttrRectGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, attrRectSuiteId);
    assert.equal(report.adapter.id, attrRectAdapterId);
    assert.match(report.adapter.derivedFromAdapterId, /^ntilde-owned-erase-attrs-region-grading-v1$/);
    assert.equal(report.snapshots.length, 3 + 16 + 14 + 13);
    assert.deepEqual(report.cases.map(item => item.caseId), ids);
});

test('the exact pinned fixture bytes were fed and the pinned code ran over the observed screens', () => {
    const { report, run } = load();
    assert.deepEqual([...byId(report, CCARA).pinnedFeeds], ['\x1b[1;1HaaaaaZ\x1b[2;1HbbbbbY\x1b[3;1HcccccX', '\x1b[1;1H', '\x1b[1;1;3;5;7$r']);
    assert.deepEqual([...byId(report, RARA).pinnedFeeds], ['\x1b[1;1H\x1b[7maaaaa\x1b[0mZ\x1b[2;1H\x1b[7mbbbbb\x1b[0mY\x1b[3;1H\x1b[7mccccc\x1b[0mX', '\x1b[1;1H', '\x1b[1;1;3;5;7$t']);
    assert.deepEqual([...byId(report, SACE).pinnedFeeds], ['\x1b[1;1H\x1b[2*x']);
    for (const [index, item] of report.cases.entries()) {
        for (const feed of item.pinnedFeeds) assert.ok(run.cases[index].feeds.includes(feed), JSON.stringify(feed));
        assert.equal(run.cases[index].pinnedFed, item.pinnedFeeds.length, 'every pinned write was replayed');
    }
});

test('pinned verdicts, separately identified effect verdicts and the divergences are recorded apart', () => {
    const { report } = load();
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.effectContract.outcome, item.conclusion.outcome]), [
        [CCARA, 'inconclusive', 'supported', 'inconclusive'], [RARA, 'inconclusive', 'supported', 'inconclusive'], [SACE, 'inconclusive', 'supported', 'inconclusive']]);
    for (const id of [CCARA, RARA]) {
        const item = byId(report, id);
        assert.equal(item.pinned.note, id === CCARA ? 'DECCARA character or outside attribute control changed' : 'DECRARA character or outside attribute control changed');
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-fixture-assumes-rectangle-extent' });
        assert.equal(item.effectContract.defaultExtent, 'stream');
        assert.equal(item.conclusion.grading, 'pinned-termless-preserved-extent-default');
        assert.match(item.effectContract.attribution, /not the pinned probe/);
        assert.equal(item.rectangleExperiment.outcome, 'supported', 'the pinned code over the pinned bytes with the rectangle extent selected first');
        assert.match(item.rectangleExperiment.policy, /labeled experiment, not a pinned grade/);
    }
    const decsace = byId(report, SACE);
    assert.equal(decsace.pinned.note, 'Sequence consumption does not measure attribute-change extent');
    assert.deepEqual(decsace.pinned.observation, { outcome: 'inconclusive', evidence: 'consumed', reason: 'insufficient-evidence' });
    assert.deepEqual(decsace.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
    assert.equal(decsace.effectContract.id, attrRectEffectContractId);
    assert.equal(decsace.conclusion.grading, 'pinned-termless-preserved-by-design');
});

test('the default extent is the stream: the pinned fixture changed its right flank; with the rectangle selected it did not', () => {
    const { report } = load();
    const set = byId(report, CCARA).observed;
    assert.deepEqual(set.geometry, { rows: 40, cols: 140 });
    assert.equal(set.reverseVideo, false);
    assert.deepEqual(set.seed.text.slice(0, 3), ['aaaaaZ', 'bbbbbY', 'cccccX']);
    assert.deepEqual(set.pinnedDefaultExtent.marks.slice(0, 3), ['IIIIII', 'IIIIII', 'IIIII.']);
    assert.deepEqual(set.pinnedDefaultExtent.text.slice(0, 3), ['aaaaaZ', 'bbbbbY', 'cccccX'], 'text preserved');
    assert.deepEqual(set.pinnedDefaultExtent.cursor, [0, 0], 'the cursor did not move');
    assert.deepEqual(set.rectangleExperiment.marks.slice(0, 3), ['IIIII.', 'IIIII.', 'IIIII.']);
    assert.equal(set.pinnedDefaultExtent.matchesStream, true);
    assert.equal(set.rectangleExperiment.matchesRectangle, true);
    assert.equal(set.witness.matchesStream, true, 'the stream was restored after the rectangle controls');
    const reverse = byId(report, RARA).observed;
    assert.deepEqual(reverse.seed.marks.slice(0, 3), ['IIIII.', 'IIIII.', 'IIIII.']);
    assert.deepEqual(reverse.pinnedDefaultExtent.marks.slice(0, 3), ['.....I', '.....I', '......']);
    assert.deepEqual(reverse.rectangleExperiment.marks.slice(0, 3), ['......', '......', '......']);
});

test('DECCARA set and clear exactly the named attributes, kept the cursor and the SGR pen, followed DECOM and ignored other selectors', () => {
    const { controls, pen, bounds, unknownSelectors } = byId(load().report, CCARA).observed;
    assert.deepEqual(bounds.marks.slice(0, 4), ['......', '.IIII.', '.IIII.', '......']);
    assert.deepEqual(pen.cursor, [3, 1]);
    assert.equal(pen.marks[3], 'B.....', 'the character written after DECCARA carries the unchanged pen (bold), not the changed inverse');
    assert.deepEqual(pen.marks.slice(0, 2), ['III...', 'III...']);
    assert.equal(unknownSelectors.marks[0], '......', 'selector 99 alone changed nothing');
    assert.equal(unknownSelectors.marks[2], '......', 'selector 8 (not a VT510 operand) changed nothing');
    assert.match(unknownSelectors.marks[1], /^\[BU\]\[BU\]\[BU\]\.\.\.$/, 'the defined selectors in the same list applied around 99');
    assert.equal(controls.pre.graded && controls.clear.graded && controls.origin.graded, true);
    assert.deepEqual(controls.pre.after.cursor, [5, 10]);
    assert.equal(controls.origin.after.buffer.top, 1);
    assert.equal(controls.origin.after.buffer.bottom, 3);
    assert.deepEqual(controls.origin.after.marks, ['......', '......', '.III..', '.III..', '.III..', '.III..'], 'rows offset by the top margin, not clipped by the bottom margin');
});

test('DECRARA toggled, restored on the second application and followed the operand table', () => {
    const { toggle, operands, pen } = byId(load().report, RARA).observed;
    assert.equal(toggle.restored, true);
    assert.notDeepEqual(toggle.before.marks, toggle.after.marks);
    assert.deepEqual(toggle.second.marks, toggle.before.marks);
    assert.deepEqual(pen.cursor, [3, 1]);
    assert.equal(operands.before.marks.length, 6);
    assert.equal(operands.after.marks.length, 6);
    assert.equal(operands.after.marks[3], operands.before.marks[3], 'selectors 22/24/25/27 are not DECRARA operands');
    assert.equal(operands.after.marks[4], operands.before.marks[4], 'a repeated selector cancels');
});

test('DECSACE: stream (0, 1, omitted, default) and rectangle (2) cells were told apart across the first, intermediate and last rows', () => {
    const observed = byId(load().report, SACE).observed;
    const streamRows = ['..IIIIII', 'IIIIIIII', 'IIIIIIII', 'IIIIII..'], rectRows = ['..IIII..', '..IIII..', '..IIII..', '..IIII..'];
    assert.deepEqual(observed.fixture.marks, ['........', '........', '........', '........']);
    for (const role of ['stream0', 'stream1', 'omitted', 'witness']) assert.deepEqual(observed[role].marks, streamRows, role);
    assert.deepEqual(observed.rectangle.marks, rectRows);
    assert.equal(observed.ignoredValue.keptRectangle, true, 'observed, not graded: an undefined value left the rectangle in force');
    assert.deepEqual(observed.rectReverse.marks, rectRows);
    assert.deepEqual(observed.streamReverse.marks, streamRows);
    assert.deepEqual([observed.isolation.buffer.top, observed.isolation.buffer.bottom, observed.isolation.buffer.origin], [1, 2, true]);
    assert.deepEqual(observed.isolation.cursor, [2, 2]);
    assert.deepEqual(observed.isolation.pen.cursor, [2, 3]);
    assert.match(observed.extentObservation, /not readable/);
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', async () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 33);
    assert.equal(receipt.attrRectEvidence.suiteId, attrRectSuiteId);
    assert.equal(receipt.attrRectEvidence.runId, summary.context.runId);
    const { counts, effectCounts } = attrRectCounts(report);
    assert.deepEqual(counts, { inconclusive: 3 });
    assert.deepEqual(effectCounts, { supported: 3 });
    assert.deepEqual(receipt.attrRectEvidence.counts, counts);
    assert.deepEqual(receipt.attrRectEvidence.effectCounts, effectCounts);
    assert.equal(receipt.attrRectEvidence.adapterBundleSha256, summary.context.attrRectBundleSha256);
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.attrRectEvidence.sha256, observationsHash);
    assert.equal(receipt.attrRectEvidence.runSha256, runHash);
    assert.equal(summary.retained.attrRectObservationsSha256, observationsHash);
    assert.equal(summary.retained.attrRectRunSha256, runHash);
    assert.equal(summary.retained.attrRectJournalSha256, journalHash);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v27');
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.ownedRunId, summary.context.runId);
    assert.notEqual(summary.stockCollectorRunId, summary.ownedRunId);
    // The predecessor inventory is derived from the merged contracts, not assumed: every other benchmark suite's case ids.
    const expected = [];
    for (const suite of benchmarkSuites.filter(item => item.suite !== 'attr-rect')) {
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
    assert.ok(!ids.some(id => unique.has(id)), 'the three new cases are counted separately');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock grades are carried, not re-measured: all three pinned cases are inconclusive there', () => {
    const { summary } = load();
    assert.equal(summary.stockCollector.rerun, false);
    assert.deepEqual(summary.stockCollector.carriedCounts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of ids) assert.equal(summary.stockCollector.probes[id].outcome, 'inconclusive', id);
    assert.match(summary.stockCollector.note, /was not re-run/);
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of readdirSync(dir)) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('prior publications still rederive beside this batch and every superseded attempt is preserved', () => {
    const earlier = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-09-erase-attrs-region/summary.json', import.meta.url)));
    assert.equal(earlier.receipt.schemaVersion, 32);
    assert.equal(earlier.receipt.attrRectEvidence, undefined);
    const { attempts, context } = load().summary;
    assert.ok(attempts.length >= 2);
    assert.equal(attempts.at(-1).runId, context.runId);
    for (const attempt of attempts.slice(0, -1)) assert.notEqual(attempt.runId, context.runId);
    assert.ok(attempts.every(attempt => attempt.receiptCompleted === true));
    assert.ok(attempts.every(attempt => Object.values(attempt.verdicts).length === 3));
});
