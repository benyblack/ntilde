// Frozen real-App grading publication. Synthetic fixtures do not replace this
// measurement; every digest below is checked against the committed evidence.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { buildSgrColorsGradingReport } from './sgr-colors-grading.mjs';
import { sgrColorsAdapterId } from './sgr-colors-grading-collector.mjs';
import { sgrColorsSuiteId } from './sgr-colors-contracts.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const path = new URL('../../docs/qa/terminfo/2026-11-06-sgr-colors-qualification.json', import.meta.url);

test('the published SGR-colors grading evidence re-derives exactly from its retained journal and run log', () => {
    const data = JSON.parse(readFileSync(path));
    const report = data.sgrColorsReport;
    const derived = buildSgrColorsGradingReport(report.snapshots, data.sgrColorsRun, data.context.runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, sgrColorsSuiteId);
    assert.equal(report.adapter.id, sgrColorsAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-ascii-sgr-grading-v1');
    assert.equal(report.snapshots.length, 8);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.conclusion.outcome, item.pinned.observation.outcome]),
        [['sgr.selective-reset.bold', 'supported', 'supported'],
            ['sgr.selective-reset.italic', 'supported', 'supported'],
            ['sgr.reset', 'supported', 'supported'],
            ['sgr.fg.standard', 'supported', 'inconclusive'],
            ['sgr.bg.standard', 'supported', 'inconclusive']]);
});

test('every graded conclusion is bound to the real run, contract bytes and receipt', () => {
    const data = JSON.parse(readFileSync(path));
    const receipt = data.receipt;
    assert.equal(receipt.schemaVersion, 14);
    assert.equal(receipt.sgrColorsEvidence.suiteId, sgrColorsSuiteId);
    assert.equal(receipt.sgrColorsEvidence.runId, data.context.runId);
    assert.equal(receipt.sgrColorsEvidence.contractsVersion, 1);
    assert.equal(receipt.sgrColorsEvidence.adapterBundleSha256, data.context.sgrColorsBundleSha256);
    const counts = {};
    for (const item of data.sgrColorsReport.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    assert.deepEqual(receipt.sgrColorsEvidence.counts, counts);
    for (const item of data.sgrColorsReport.cases) {
        assert.ok(item.barriers.every(barrier => /^\x1b\[\?[\d;]+c$/.test(barrier)));
        assert.equal(item.pinned.observation.evidence, 'parser-state');
    }
    // Selective resets prove the target was active, cleared, and the unrelated
    // attribute survived; the color cases expose exact stored palette indices.
    const bold = data.sgrColorsReport.cases.find(item => item.caseId === 'sgr.selective-reset.bold');
    assert.equal(bold.observed.before.bold, true);
    assert.equal(bold.observed.before.dim, true);
    assert.equal(bold.observed.after.bold, false);
    assert.equal(bold.observed.after.dim, false);
    assert.equal(bold.observed.after.italic, true);
    const italic = data.sgrColorsReport.cases.find(item => item.caseId === 'sgr.selective-reset.italic');
    assert.equal(italic.observed.before.italic, true);
    assert.equal(italic.observed.after.italic, false);
    assert.equal(italic.observed.after.bold, true);
    const reset = data.sgrColorsReport.cases.find(item => item.caseId === 'sgr.reset');
    assert.equal(reset.observed.before.underline, true);
    assert.equal(reset.observed.after.defaultColors, true);
    const fg = data.sgrColorsReport.cases.find(item => item.caseId === 'sgr.fg.standard');
    assert.equal(fg.observed.first.fgIndex, 1);
    assert.equal(fg.observed.second.fgIndex, 4);
    const bg = data.sgrColorsReport.cases.find(item => item.caseId === 'sgr.bg.standard');
    assert.equal(bg.observed.first.bgIndex, 1);
    assert.equal(bg.observed.second.bgIndex, 4);
});

test('the untouched stock run still grades all five cases inconclusive', () => {
    const data = JSON.parse(readFileSync(path));
    assert.deepEqual(data.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(data.stockCollector.suiteComplete, false);
    for (const probeId of ['sgr.reset', 'sgr.selective-reset.bold', 'sgr.selective-reset.italic',
        'sgr.fg.standard', 'sgr.bg.standard']) {
        const observation = data.stockCollector.probes[probeId];
        assert.equal(observation.outcome, 'inconclusive', probeId);
        assert.equal(observation.evidence, 'consumed', probeId);
    }
    assert.equal(data.stockCollector.probes['sgr.fg.standard'].note, 'Cursor advance does not verify SGR styling');
});

test('failed preparation attempts are preserved, not silently replaced', () => {
    const data = JSON.parse(readFileSync(path));
    assert.ok(Array.isArray(data.attempts) && data.attempts.length >= 1);
    for (const attempt of data.attempts) {
        assert.match(attempt.error, /exited before collection|private clipboard collection failed|is not defined/);
        assert.match(attempt.directory, /owned-[A-Za-z0-9]{6}$/);
    }
});

test('published digest declarations match the reconstructed retained bytes', async () => {
    const data = JSON.parse(readFileSync(path));
    // The report was written with indent 2; the run log compact; the journal
    // one compact frame per line. Reconstruct each and compare digests.
    assert.equal(hash(JSON.stringify(data.sgrColorsReport, null, 2) + '\n'),
        data.retained.sgrColorsObservationsSha256);
    assert.equal(hash(JSON.stringify(data.sgrColorsRun) + '\n'), data.retained.sgrColorsRunSha256);
    assert.equal(hash(data.sgrColorsReport.snapshots.map(frame => JSON.stringify(frame)).join('\n') + '\n'),
        data.retained.sgrColorsJournalSha256);
    assert.equal(data.retained.collectorSgrColorsSha256, data.receipt.sgrColorsEvidence.adapterBundleSha256);
    assert.equal(data.retained.sgrColorsObservationsSha256, data.receipt.sgrColorsEvidence.sha256);
    assert.equal(data.retained.sgrColorsRunSha256, data.receipt.sgrColorsEvidence.runSha256);
    // The measured run used the frozen 113-file v7 inventory.
    assert.equal(data.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v7');
    const { capturedV14SourceFiles, capturedV15SourceFiles } = await import('./owned-sources.mjs');
    assert.equal(Object.keys(capturedV14SourceFiles).length, 113);
    assert.equal(Object.keys(capturedV14SourceFiles).includes('gradedPlanSourceSha256'), false);
    assert.equal(Object.keys(capturedV15SourceFiles).includes('gradedPlanSourceSha256'), true);
});

test('older publications remain byte-for-byte unchanged beside this batch', () => {
    const asciiSgr = readFileSync(new URL('../../docs/qa/terminfo/2026-11-05-ascii-sgr-qualification.json', import.meta.url));
    assert.equal(asciiSgr.includes('sgrColorsReport'), false);
    const query = readFileSync(new URL('../../docs/qa/terminfo/2026-11-05-query-observations.json', import.meta.url));
    assert.equal(hash(query), '37c023b1b554b23aa7f18b0b1bcf1b0126c3863cfa4b75224f11de01d7cc0d7d');
    const reset = readFileSync(new URL('../../docs/qa/terminfo/2026-11-04-reset-observations.json', import.meta.url));
    assert.equal(hash(reset), '30699be1b2f132bd5a572e2f8a0a235ed4c3021c003482ca90386a2a6af32e23');
});
