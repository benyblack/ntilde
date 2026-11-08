// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is
// checked against the committed evidence. The publication is split into reviewable files (summary, report, run
// log, common frames and one file per read frame); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildSgrResetGradingReport } from './sgr-reset-grading.mjs';
import { sgrResetAdapterId } from './sgr-reset-grading-collector.mjs';
import { sgrResetSuiteId, sgrResetContracts } from './sgr-reset-contracts.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-08-sgr-selective-reset/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const ids = ['sgr.selective-reset.underline', 'sgr.selective-reset.inverse'];
const load = () => {
    const summary = read('summary.json'), report = read('report.json'), run = read('run-log.json'), common = read('journal-common.json');
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of sgrResetContracts) {
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

test('the published selective-reset evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildSgrResetGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, sgrResetSuiteId);
    assert.equal(report.adapter.id, sgrResetAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-sgr-underline-color-grading-v1');
    assert.equal(report.snapshots.length, 3 + 8 + 6);
    assert.deepEqual(report.cases.map(item => item.caseId), ids);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.pinned.observation.outcome]), ids.map(() => ['supported', 'supported']));
    for (const item of report.cases) assert.equal(item.divergence, undefined);
});

test('SGR 24: underline is active before the reset, cleared (flag and style 0) after it, with bold preserved and nothing else moved', () => {
    const [underline] = load().report.cases.map(item => item.observed);
    const [c, x, y] = underline.pinned;
    assert.deepEqual([c.char, c.underline, c.bold, c.flags], ['C', false, false, 768]);
    assert.deepEqual([x.char, x.underline, x.underlineStyle, x.bold], ['X', 'single', 1, true]);
    assert.deepEqual([y.char, y.underline, y.underlineStyle, y.bold, y.flags & 0x0008], ['Y', false, 0, true, 0]);
    for (const cell of underline.pinned) {
        assert.deepEqual([cell.fgIndex, cell.bgIndex, cell.foregroundArgb, cell.backgroundArgb, cell.overline, cell.underlineColorIndex, cell.underlineArgb], [-1, -1, null, null, false, -1, null]);
    }
    // Controls: no reset keeps it, SGR 22 keeps underline but drops bold, SGR 0 drops both, SGR 59 keeps the underline.
    assert.equal(underline.controls.noReset[2].underline, 'single');
    const wrong = underline.controls['wrongReset(SGR 22)'][2];
    assert.deepEqual([wrong.underline, wrong.bold], ['single', false]);
    assert.deepEqual([underline.controls.fullReset[2].underline, underline.controls.fullReset[2].bold], [false, false]);
    assert.equal(underline.controls.doubleUnderlineThenSgr24[2].underline, false);
    assert.equal(underline.controls.sgr59KeepsUnderline[2].underline, 'single');
    // Underline color is documented, not required.
    assert.match(underline.underlineColorBehavior.policy, /not required/);
    assert.equal(underline.underlineColorBehavior.requestedLiteralStored, true);
    assert.equal(underline.underlineColorBehavior.sgr59KeepsUnderline, true);
});

test('SGR 27: per-cell inverse is active before the reset, cleared after it, with DECSCNM off and a separate DECSCNM-on control', () => {
    const { report } = load();
    const inverse = report.cases[1].observed;
    const [c, x, y] = inverse.pinned;
    assert.deepEqual([c.inverse, x.inverse, y.inverse, x.bold, y.bold], [false, true, false, true, true]);
    assert.deepEqual([x.flags & 0x0002, y.flags & 0x0002], [2, 0]);
    for (const cell of inverse.pinned) assert.deepEqual([cell.fgIndex, cell.bgIndex, cell.foregroundArgb, cell.backgroundArgb], [-1, -1, null, null]);
    assert.equal(inverse.reverseVideo, false);
    assert.equal(inverse.reverseControl, true);
    assert.deepEqual(inverse.controls.decscnmOn.map(cell => cell.flags), inverse.pinned.map(cell => cell.flags));
    assert.equal(inverse.controls.noReset[2].inverse, true);
    assert.equal(inverse.controls['wrongReset(SGR 24)'][2].inverse, true);
    assert.equal(inverse.controls.fullReset[2].bold, false);
    const flags = report.snapshots.map(frame => frame.screen.colorBinding.reverseVideo);
    assert.equal(flags.filter(Boolean).length, 1);
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 23);
    assert.equal(receipt.sgrResetEvidence.suiteId, sgrResetSuiteId);
    assert.equal(receipt.sgrResetEvidence.runId, summary.context.runId);
    assert.deepEqual(receipt.sgrResetEvidence.counts, { supported: 2 });
    assert.equal(receipt.sgrResetEvidence.adapterBundleSha256, summary.context.sgrResetBundleSha256);
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.sgrResetEvidence.sha256, observationsHash);
    assert.equal(receipt.sgrResetEvidence.runSha256, runHash);
    assert.equal(summary.retained.sgrResetObservationsSha256, observationsHash);
    assert.equal(summary.retained.sgrResetRunSha256, runHash);
    assert.equal(summary.retained.sgrResetJournalSha256, journalHash);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v17');
    assert.equal(receipt.fixtureRemoved, true);
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.equal(unique.size, 29, 'the prior twenty-nine unique benchmark qualifications');
    for (const suite of Object.values(summary.sameRun)) assert.ok(suite.cases.every(item => item.outcome === 'supported'));
    assert.ok(!ids.some(id => unique.has(id)), 'the two new cases are counted separately');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock run keeps its own grades; the pinned terminal path never verifies SGR reset', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of ids) assert.deepEqual(summary.stockCollector.probes[id], { outcome: 'inconclusive', note: 'Cursor advance does not verify SGR reset' });
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of readdirSync(dir)) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('prior publications still rederive beside this batch and carry no selective-reset evidence', () => {
    const resolved = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-underline-color-resolved/summary.json', import.meta.url)));
    assert.equal(resolved.receipt.schemaVersion, 22);
    assert.equal(resolved.receipt.sgrResetEvidence, undefined);
    assert.equal(load().summary.attempts.length, 0, 'no failed attempt preceded this measurement');
});
