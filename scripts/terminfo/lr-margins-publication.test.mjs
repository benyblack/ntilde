// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked against the
// committed evidence. The publication is split into reviewable files; the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildLrMarginsGradingReport } from './lr-margins-grading.mjs';
import { lrMarginsAdapterId } from './lr-margins-grading-collector.mjs';
import { lrMarginsSuiteId, lrMarginsContracts, lrMarginsCaseIds, lrMarginsFixture } from './lr-margins-contracts.mjs';
import { lrMarginsCounts } from './lr-margins-receipt.mjs';
import { benchmarkSuites } from './qualification-inventory.mjs';

const ID = 'modes.left-right-margin';
const dir = new URL('../../docs/qa/terminfo/2026-11-10-left-right-margins/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const load = () => {
    const summary = read('summary.json'), run = read('run-log.json'), common = read('journal-common.json'), header = common.reportHeader;
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of lrMarginsContracts) {
        for (let index = 0; index < contract.roles.length; index++) {
            const journal = read(`journal-${contract.caseId}-read${index}.json`);
            assert.equal(journal.caseId, contract.caseId);
            assert.equal(journal.read, index);
            snapshots.push(journal.frame);
        }
    }
    snapshots.push(common.frames.finish);
    const cases = lrMarginsCaseIds.map(id => read(`case-${id}.json`));
    return { summary, run, report: { ...header, cases, snapshots } };
};
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

test('the published evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildLrMarginsGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, lrMarginsSuiteId);
    assert.equal(report.adapter.id, lrMarginsAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-rectangle-checksum-grading-v1');
    assert.equal(report.snapshots.length, 3 + lrMarginsContracts[0].roles.length);
});

test('the pinned verdict is preserved inconclusive by design and the separately identified effect contract is supported', () => {
    const [item] = load().report.cases;
    assert.equal(item.caseId, ID);
    assert.deepEqual(item.pinnedFeeds, ['\x1b[?69h', '\x1b[?69l']);
    assert.equal(item.pinned.response, '{"mode":true}', 'the App reported DECLRMM set after the pinned ESC[?69h');
    assert.equal(item.pinned.observation.outcome, 'inconclusive');
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.conclusion.grading, 'pinned-termless-preserved-by-design');
    assert.deepEqual([item.effectContract.id, item.effectContract.outcome], ['ntilde-left-right-margins-effect-v1', 'supported']);
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
});

test('every control matched the independent margins model over the 40x140 screen', () => {
    const record = load().report.cases[0].observed;
    assert.deepEqual(record.geometry, { rows: 40, cols: 140 });
    assert.equal(record.reverseVideo, false);
    assert.deepEqual(record.pinnedReads, { set: { mode: true, margins: [0, 139] }, reset: { mode: false, margins: [0, 139] } });
    assert.deepEqual(Object.keys(record.controls), lrMarginsFixture.controls.map(item => item.role));
    for (const [role, control] of Object.entries(record.controls)) assert.equal(control.matchesModel, true, role);
    const { controls } = record;
    assert.deepEqual([controls.setMargins.margins, controls.setMargins.mode, controls.setMargins.cursor], [[4, 13], true, [0, 0]]);
    assert.deepEqual([controls.scosc.margins, controls.scosc.mode, controls.scosc.cursor], [[0, 139], false, [1, 2]], 'without DECLRMM, CSI 5;14 s saved the cursor');
    assert.deepEqual(controls.refusals.margins, [4, 13], 'the equal and inverted pairs were ignored');
    assert.deepEqual([controls.decstr.margins, controls.decstr.mode], [[0, 139], true], 'DECSTR restored full width and kept DECLRMM');
    assert.deepEqual([controls.modeReset.margins, controls.modeReset.mode], [[0, 139], false]);
    assert.equal(controls.moves.text[7].indexOf('T'), 13, 'HT from column 11 stopped at the right margin instead of the stop at 16');
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', async () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 40);
    assert.equal(receipt.lrMarginsEvidence.runId, summary.context.runId);
    assert.deepEqual(lrMarginsCounts(report), { 'inconclusive/supported': 1 });
    assert.deepEqual(receipt.lrMarginsEvidence.counts, lrMarginsCounts(report));
    assert.equal(receipt.lrMarginsEvidence.adapterBundleSha256, summary.context.lrMarginsBundleSha256);
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    assert.equal(receipt.lrMarginsEvidence.sha256, observationsHash);
    assert.equal(receipt.lrMarginsEvidence.runSha256, hash(JSON.stringify(run) + '\n'));
    assert.equal(summary.retained.lrMarginsJournalSha256, hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join('')));
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v34');
    assert.equal(receipt.fixtureRemoved, true);
    const predecessors = benchmarkSuites.slice(0, benchmarkSuites.findIndex(item => item.suite === 'lr-margins'));
    const expected = [];
    for (const suite of predecessors) expected.push(...(await import(new URL(suite.module, import.meta.url)))[suite.exports[2]]);
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...new Set(expected)].sort());
    assert.equal(Object.keys(summary.sameRun).length, predecessors.length);
    for (const [name, suite] of Object.entries(summary.sameRun)) {
        if (suite.cases.some(item => item.effectContract)) assert.ok(suite.cases.every(item => item.effectContract === 'supported'), name);
        else assert.ok(suite.cases.every(item => item.outcome === 'supported'), name);
    }
    assert.ok(!unique.has(ID));
    assert.deepEqual(summary.queryCounts, { matched: 33, mismatched: 7 }, 'the query suite reconfirms with the same frozen mismatches');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.equal(summary.underlineResolved.cases.length, 3);
});

test('the stock run keeps modes.left-right-margin inconclusive (the pinned terminal path never decides)', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 167, supported: 87, unsupported: 1 });
    assert.deepEqual([summary.stockCollector.probes[ID].outcome, summary.stockCollector.probes[ID].evidence], ['inconclusive', 'none']);
});

test('revisions, attempts and file sizes', () => {
    const { summary } = load();
    assert.match(summary.appRevision, /^[0-9a-f]{40}$/);
    assert.match(summary.scriptsRevision, /^[0-9a-f]{40}$/);
    assert.equal(summary.attempts.length, 1);
    assert.equal(summary.attempts[0].runId, summary.context.runId);
    for (const name of readdirSync(dir)) assert.ok(readFileSync(new URL(name, dir), 'utf8').split('\n').length <= 1500, name);
});
