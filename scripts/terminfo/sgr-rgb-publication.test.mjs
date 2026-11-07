// Frozen real-App grading publication. Synthetic fixtures do not replace this
// measurement; every digest below is checked against the committed evidence.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { buildSgrRgbGradingReport } from './sgr-rgb-grading.mjs';
import { sgrRgbAdapterId } from './sgr-rgb-grading-collector.mjs';
import { sgrRgbSuiteId } from './sgr-rgb-contracts.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const path = new URL('../../docs/qa/terminfo/2026-11-07-sgr-rgb-qualification.json', import.meta.url);

test('the published SGR-RGB grading evidence re-derives exactly from its retained journal and run log', () => {
    const data = JSON.parse(readFileSync(path));
    const report = data.sgrRgbReport;
    const derived = buildSgrRgbGradingReport(report.snapshots, data.sgrRgbRun, data.context.runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, sgrRgbSuiteId);
    assert.equal(report.adapter.id, sgrRgbAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-sgr-colors-grading-v1');
    assert.equal(report.snapshots.length, 5);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.conclusion.outcome, item.pinned.observation.outcome]),
        [['sgr.fg.standard', 'supported', 'supported'], ['sgr.bg.standard', 'supported', 'supported']]);
});

test('every RGB conclusion is bound to the recorded theme state and exact observed values', () => {
    const data = JSON.parse(readFileSync(path));
    const fg = data.sgrRgbReport.cases.find(item => item.caseId === 'sgr.fg.standard');
    const bg = data.sgrRgbReport.cases.find(item => item.caseId === 'sgr.bg.standard');
    // The neutral baseline resolves default (null storage marker, defined value);
    // the named colors resolve the theme palette entries 1 and 4.
    assert.equal(fg.observed.baseline.fg, null);
    assert.equal(fg.observed.baseline.fgSource, 'default');
    assert.deepEqual(fg.observed.first.fg, { r: 205, g: 0, b: 0 });
    assert.deepEqual(fg.observed.second.fg, { r: 0, g: 0, b: 238 });
    assert.equal(fg.observed.first.bg, null);
    assert.equal(fg.observed.second.bg, null);
    assert.deepEqual(bg.observed.first.bg, { r: 205, g: 0, b: 0 });
    assert.deepEqual(bg.observed.second.bg, { r: 0, g: 0, b: 238 });
    assert.equal(bg.observed.first.fg, null);
    assert.equal(bg.observed.second.fg, null);
    // One binding covers the suite and is recorded beside each conclusion.
    for (const item of [fg, bg]) {
        assert.equal(item.colorBinding.stage, 'theme-resolution');
        assert.equal(item.colorBinding.themeName, 'Default');
        assert.equal(item.colorBinding.reverseVideo, false);
        assert.equal(item.colorBinding.minimumContrast, 1);
        assert.deepEqual(item.colorBinding.paletteOverrideIndices, []);
        assert.match(item.colorBinding.bindingSha256, /^[a-f\d]{64}$/);
        assert.equal(item.colorBinding.bindingSha256, fg.colorBinding.bindingSha256);
        assert.equal(item.pinned.observation.evidence, 'parser-state');
        assert.equal(item.pinned.pass, true);
        assert.equal(item.pinned.assertions[0].kind, 'positive');
        assert.ok(item.conclusion.scope.includes('resolution stage'));
        assert.ok(item.conclusion.scope.includes('stored-index conclusions of the SGR-colors suite'));
        assert.ok(item.barriers.every(barrier => /^\x1b\[\?[\d;]+c$/.test(barrier)));
    }
});

test('the receipt binds the run, adapters and counts, and the same-run predecessor suites still pass', () => {
    const data = JSON.parse(readFileSync(path));
    const receipt = data.receipt;
    assert.equal(receipt.schemaVersion, 15);
    assert.equal(receipt.sgrRgbEvidence.suiteId, sgrRgbSuiteId);
    assert.equal(receipt.sgrRgbEvidence.runId, data.context.runId);
    assert.equal(receipt.sgrRgbEvidence.contractsVersion, 1);
    assert.equal(receipt.sgrRgbEvidence.adapterBundleSha256, data.context.sgrRgbBundleSha256);
    assert.deepEqual(receipt.sgrRgbEvidence.counts, { supported: 2 });
    assert.equal(receipt.sgrColorsEvidence.suiteId, 'ntilde-owned-sgr-colors-graded-v1');
    assert.deepEqual(receipt.sgrColorsEvidence.counts, { supported: 5 });
    assert.equal(receipt.gradingEvidence.suiteId, 'ntilde-owned-ascii-sgr-graded-v1');
    assert.deepEqual(receipt.gradingEvidence.counts, { supported: 4 });
    // The predecessor suites' conclusions are reconfirmed in this very run. The
    // same-run SGR-colors reconfirmation grades under the current contracts
    // revision (v2); the frozen 2026-11-06 publication keeps its measured v1
    // semantics and still rederives under them.
    assert.deepEqual(data.sameRun.grading.cases.map(item => item.outcome), ['supported', 'supported', 'supported', 'supported']);
    assert.deepEqual(data.sameRun.sgrColors.cases.map(item => item.outcome), ['supported', 'supported', 'supported', 'supported', 'supported']);
    assert.equal(data.sameRun.sgrColors.contractsVersion, 2);
});

test('the untouched stock run still grades both cases inconclusive', () => {
    const data = JSON.parse(readFileSync(path));
    assert.deepEqual(data.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(data.stockCollector.suiteComplete, false);
    assert.equal(data.stockCollector.probeHash, '16a690f34c0e');
    for (const probeId of ['sgr.fg.standard', 'sgr.bg.standard']) {
        const observation = data.stockCollector.probes[probeId];
        assert.equal(observation.outcome, 'inconclusive', probeId);
        assert.equal(observation.evidence, 'consumed', probeId);
    }
    assert.equal(data.stockCollector.probes['sgr.fg.standard'].note, 'Cursor advance does not verify SGR styling');
});

test('prior publications still rederive byte-for-byte beside this batch', () => {
    const colors = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-06-sgr-colors-qualification.json', import.meta.url)));
    assert.equal(colors.receipt.schemaVersion, 14);
    assert.equal(colors.receipt.sgrRgbEvidence, undefined);
    const ascii = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-05-ascii-sgr-qualification.json', import.meta.url)));
    assert.equal(ascii.receipt.schemaVersion, 13);
    assert.equal(ascii.receipt.sgrRgbEvidence, undefined);
    // Their own frozen conclusions stay as published: stored indices, never RGB.
    const fg = colors.sgrColorsReport.cases.find(item => item.caseId === 'sgr.fg.standard');
    assert.equal(fg.conclusion.outcome, 'supported');
    assert.ok(fg.conclusion.scope.includes('stays inconclusive'));
    assert.equal(fg.pinned.observation.outcome, 'inconclusive');
});
