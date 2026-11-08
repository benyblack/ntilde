// Frozen real-App publication. Synthetic fixtures do not replace this
// measurement; every digest below is checked against the committed evidence.
// The publication is split into reviewable files (summary, report, run log and
// one journal per case); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { buildSgrUnderlineGradingReport } from './sgr-underline-grading.mjs';
import { sgrUnderlineAdapterId } from './sgr-underline-grading-collector.mjs';
import { sgrUnderlineSuiteId, sgrUnderlineContracts, sgrUnderlineLadder } from './sgr-underline-contracts.mjs';
const dir = new URL('../../docs/qa/terminfo/2026-11-08-sgr-underline/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const ids = ['sgr.underline.double', 'sgr.underline.curly', 'sgr.underline.dotted', 'sgr.underline.dashed'];
const load = () => {
    const summary = read('summary.json'), report = read('report.json'), run = read('run-log.json'), common = read('journal-common.json');
    const snapshots = [common.frames.start, common.frames.bind];
    for (const id of ids) {
        const journal = read(`journal-${id}.json`);
        assert.equal(journal.caseId, id);
        snapshots.push(...journal.frames);
    }
    snapshots.push(common.frames.finish);
    return { summary, run, report: { ...report, snapshots } };
};

test('the published SGR-underline evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildSgrUnderlineGradingReport(report.snapshots, run, summary.context.runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, sgrUnderlineSuiteId);
    assert.equal(report.adapter.id, sgrUnderlineAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-sgr-truecolor-grading-v1');
    assert.equal(report.snapshots.length, 3 + 4 * 4);
    assert.deepEqual(report.cases.map(item => item.caseId), ids);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.pinned.observation.outcome]), ids.map(() => ['supported', 'supported']));
    assert.equal(sgrUnderlineContracts.length, 4);
});

test('every conclusion is bound to the exact stored underline style, flag and labeled ladder', () => {
    const { report } = load();
    const expected = { 'sgr.underline.double': [2, 'double', 'SGR 21'], 'sgr.underline.curly': [3, 'curly', 'SGR 4:3'],
        'sgr.underline.dotted': [4, 'dotted', 'SGR 4:4'], 'sgr.underline.dashed': [5, 'dashed', 'SGR 4:5'] };
    for (const item of report.cases) {
        const [code, name, spelling] = expected[item.caseId];
        assert.equal(item.observed.target.underlineStyle, code);
        assert.equal(item.observed.target.underline, name);
        assert.equal(item.observed.target.flags & 0x08, 0x08, 'the generic underline flag is set alongside the exact style');
        assert.match(item.conclusion.scope, /not claimed/);
        assert.equal(item.divergence, undefined);
        assert.equal(item.observed.reverseVideo, false);
        assert.equal(item.pinned.assertions[0].kind, 'positive');
        assert.equal(item.pinned.assertions[0].expected, `${item.caseId}: attribute applied to the rendered X cell`);
        assert.equal(sgrUnderlineContracts.find(contract => contract.caseId === item.caseId).spelling, spelling);
        // Baseline and the SGR 24 restored cell are neutral; X keeps its style after SGR 24.
        assert.deepEqual([item.observed.baseline.underlineStyle, item.observed.trailing[1].underlineStyle], [0, 0]);
        assert.equal(item.observed.trailing[0].underlineStyle, code);
        // The labeled ladder shows every spelling, SGR 21 and SGR 4:2 separately.
        assert.deepEqual(item.observed.controlLadder.map(cell => [cell.spelling, cell.underline]), [
            ...sgrUnderlineLadder.map(entry => [entry.spelling, entry.style]), ['neutral (after SGR 0)', false]]);
    }
});

test('the receipt binds the run, adapters and counts, and every predecessor and the finding reconfirm in the same run', () => {
    const { summary } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 20);
    assert.equal(receipt.sgrUnderlineEvidence.suiteId, sgrUnderlineSuiteId);
    assert.equal(receipt.sgrUnderlineEvidence.runId, summary.context.runId);
    assert.equal(receipt.sgrUnderlineEvidence.contractsVersion, 1);
    assert.equal(receipt.sgrUnderlineEvidence.adapterBundleSha256, summary.context.sgrUnderlineBundleSha256);
    assert.deepEqual(receipt.sgrUnderlineEvidence.counts, { supported: 4 });
    assert.deepEqual(receipt.sgrTruecolorEvidence.counts, { supported: 2 });
    assert.deepEqual(receipt.sgrPaletteEvidence.counts, { supported: 6 });
    assert.deepEqual(receipt.sgrAttrsEvidence.counts, { supported: 5 });
    assert.deepEqual(receipt.sgrRgbEvidence.counts, { supported: 2 });
    assert.deepEqual(receipt.sgrColorsEvidence.counts, { supported: 5 });
    assert.deepEqual(receipt.gradingEvidence.counts, { supported: 4 });
    assert.deepEqual(receipt.truecolorFindingEvidence.counts, { supported: 10 });
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v14');
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.equal(unique.size, 22, 'the prior twenty-two unique qualifications');
    for (const suite of Object.values(summary.sameRun)) assert.ok(suite.cases.every(item => item.outcome === 'supported'));
    // The finding is separate: its ids are never benchmark cases and never join the unique count.
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.ok(!unique.has(summary.truecolorFinding.cases[0].caseId));
});

test('the untouched stock run keeps its own grades and all four cases stay inconclusive there', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of ids) {
        assert.equal(summary.stockCollector.probes[id].outcome, 'inconclusive', id);
        assert.equal(summary.stockCollector.probes[id].note, 'Cursor advance does not verify SGR styling', id);
    }
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of ['summary.json', 'report.json', 'run-log.json', 'journal-common.json', ...ids.map(id => `journal-${id}.json`)]) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('failed preparation attempts are preserved and prior publications still rederive beside this batch', () => {
    const { summary } = load();
    assert.equal(summary.attempts.length, 2);
    assert.ok(summary.attempts.every(attempt => attempt.receiptCompleted === false));
    const truecolor = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-truecolor/summary.json', import.meta.url)));
    assert.equal(truecolor.receipt.schemaVersion, 18);
    assert.equal(truecolor.receipt.sgrUnderlineEvidence, undefined);
    const finding = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-truecolor-literal-finding/summary.json', import.meta.url)));
    assert.equal(finding.after.receiptSchemaVersion, 19);
});
