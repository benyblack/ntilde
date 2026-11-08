// Frozen real-App publication. Synthetic fixtures do not replace this
// measurement; every digest below is checked against the committed evidence.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { buildSgrAttrsGradingReport } from './sgr-attrs-grading.mjs';
import { sgrAttrsAdapterId } from './sgr-attrs-grading-collector.mjs';
import { sgrAttrsSuiteId, sgrAttrsContracts } from './sgr-attrs-contracts.mjs';
/** The frozen publication this test rederives. */
const path = new URL('../../docs/qa/terminfo/2026-11-08-sgr-attributes-qualification.json', import.meta.url);
const load = () => JSON.parse(readFileSync(path));
const ids = ['sgr.underline.single', 'sgr.inverse', 'sgr.faint', 'sgr.hidden', 'sgr.strikethrough'];

test('the published SGR-attributes evidence re-derives exactly from its retained journal and run log', () => {
    const data = load();
    const report = data.sgrAttrsReport;
    const derived = buildSgrAttrsGradingReport(report.snapshots, data.sgrAttrsRun, data.context.runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, sgrAttrsSuiteId);
    assert.equal(report.adapter.id, sgrAttrsAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-sgr-rgb-grading-v1');
    assert.equal(report.snapshots.length, 3 + 5 * 3);
    assert.deepEqual(report.cases.map(item => item.caseId), ids);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.pinned.observation.outcome]), ids.map(() => ['supported', 'supported']));
    assert.equal(sgrAttrsContracts.length, 5);
});

test('every conclusion is bound to its exact stored observation and stays within stored-state scope', () => {
    const { sgrAttrsReport: report } = load();
    const by = id => report.cases.find(item => item.caseId === id);
    assert.deepEqual(by('sgr.underline.single').observed.target, { char: 'X', flags: 776, underline: 'single', underlineStyle: 1 });
    assert.deepEqual(by('sgr.inverse').observed.target, { char: 'X', flags: 770, inverse: true });
    assert.deepEqual(by('sgr.faint').observed.target, { char: 'X', flags: 832, dim: true, bold: false });
    assert.deepEqual(by('sgr.hidden').observed.target, { char: 'X', flags: 896, hidden: true });
    assert.deepEqual(by('sgr.strikethrough').observed.target, { char: 'X', flags: 784, strikethrough: true });
    for (const item of report.cases) {
        assert.equal(item.observed.baseline.char, 'C');
        assert.equal(item.observed.baseline.flags, 768, 'neutral default baseline');
        assert.equal(item.observed.reverseVideo, false);
        assert.equal(item.colorBinding.reverseVideo, false);
        assert.equal(item.divergence, undefined);
        assert.match(item.conclusion.scope, /not claimed/);
        assert.equal(item.conclusion.evidence, 'owned-stored-cells');
        assert.equal(item.pinned.assertions[0].kind, 'positive');
    }
    const faint = by('sgr.faint').observed.trailing;
    assert.equal(faint[1].bold && faint[1].dim, true, 'bold and faint coexist on Y');
    assert.equal(faint[2].bold || faint[2].dim, false, 'SGR 22 clears both on Z');
    assert.equal(by('sgr.hidden').observed.trailing[0].hidden, true);
});

test('overline was observed and clear on every journaled cell of the five cases', () => {
    const { sgrAttrsReport: report } = load();
    for (const frame of report.snapshots.filter(item => item.kind === 'sgattr-read')) {
        const runs = frame.screen.cellDetails;
        assert.ok(runs.every(run => run.overline === false));
    }
});

test('the receipt binds the run, adapters and counts, and every predecessor reconfirms in the same run', () => {
    const data = load();
    const { receipt } = data;
    assert.equal(receipt.schemaVersion, 16);
    assert.equal(receipt.sgrAttrsEvidence.suiteId, sgrAttrsSuiteId);
    assert.equal(receipt.sgrAttrsEvidence.runId, data.context.runId);
    assert.equal(receipt.sgrAttrsEvidence.contractsVersion, 1);
    assert.equal(receipt.sgrAttrsEvidence.adapterBundleSha256, data.context.sgrAttrsBundleSha256);
    assert.deepEqual(receipt.sgrAttrsEvidence.counts, { supported: 5 });
    assert.deepEqual(receipt.sgrRgbEvidence.counts, { supported: 2 });
    assert.deepEqual(receipt.sgrColorsEvidence.counts, { supported: 5 });
    assert.deepEqual(receipt.gradingEvidence.counts, { supported: 4 });
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(data.sameRun.grading.cases.length + data.sameRun.sgrColors.cases.length + data.sameRun.sgrRgb.cases.length, 11);
    for (const suite of Object.values(data.sameRun)) assert.ok(suite.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock run keeps its own grades and all five cases stay inconclusive there', () => {
    const data = load();
    assert.deepEqual(data.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(data.stockCollector.suiteComplete, false);
    assert.equal(data.stockCollector.probeHash, '16a690f34c0e');
    for (const id of ids) {
        assert.equal(data.stockCollector.probes[id].outcome, 'inconclusive', id);
        assert.equal(data.stockCollector.probes[id].note, 'Cursor advance does not verify SGR styling', id);
    }
});

test('failed attempts are preserved and prior publications still rederive beside this batch', () => {
    const data = load();
    assert.equal(data.attempts.length, 7);
    assert.deepEqual(data.attempts.map(attempt => attempt.receiptCompleted), [false, false, true, true, true, true, false]);
    assert.equal(data.attempts[2].outcome, 'complete, superseded');
    assert.equal(data.attempts[3].outcome, 'complete, superseded');
    assert.equal(data.attempts[4].outcome, 'complete, superseded');
    assert.equal(data.attempts[5].outcome, 'complete, superseded');
    const rgb = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-07-sgr-rgb-qualification.json', import.meta.url)));
    assert.equal(rgb.receipt.schemaVersion, 15);
    assert.equal(rgb.receipt.sgrAttrsEvidence, undefined);
    const colors = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-06-sgr-colors-qualification.json', import.meta.url)));
    assert.equal(colors.receipt.schemaVersion, 14);
    assert.equal(colors.receipt.sgrAttrsEvidence, undefined);
});
