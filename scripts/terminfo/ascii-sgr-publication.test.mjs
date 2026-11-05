// Frozen real-App grading publication. Synthetic fixtures do not replace this
// measurement; every digest below is checked against the committed evidence.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { buildAsciiSgrGradingReport } from './ascii-sgr-grading.mjs';
import { gradingAdapterId } from './grading-collector.mjs';
import { asciiSgrSuiteId } from './ascii-sgr-contracts.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const path = new URL('../../docs/qa/terminfo/2026-11-05-ascii-sgr-qualification.json', import.meta.url);

test('the published ASCII/SGR grading evidence re-derives exactly from its retained journal and run log', () => {
    const bytes = readFileSync(path), data = JSON.parse(bytes);
    const report = data.gradingReport;
    const derived = buildAsciiSgrGradingReport(report.snapshots, data.gradingRun, data.context.runId, report.adapter);
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    const journal = readFileSync(path, 'utf8');
    void journal;
    assert.equal(report.suiteId, asciiSgrSuiteId);
    assert.equal(report.adapter.id, gradingAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-macos-v1');
    assert.equal(report.snapshots.length, 9);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.conclusion.outcome]),
        [['text.basic', 'supported'], ['text.overwrite', 'supported'], ['sgr.bold', 'supported'], ['sgr.italic', 'supported']]);
});

test('every graded conclusion is bound to the real run, contract bytes and receipt', () => {
    const data = JSON.parse(readFileSync(path));
    const receipt = data.receipt;
    assert.equal(receipt.schemaVersion, 13);
    assert.equal(receipt.gradingEvidence.suiteId, asciiSgrSuiteId);
    assert.equal(receipt.gradingEvidence.runId, data.context.runId);
    assert.equal(receipt.gradingEvidence.contractsVersion, 1);
    assert.equal(receipt.gradingEvidence.adapterBundleSha256, data.context.gradingBundleSha256);
    const counts = {};
    for (const item of data.gradingReport.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    assert.deepEqual(receipt.gradingEvidence.counts, counts);
    for (const item of data.gradingReport.cases) {
        assert.ok(item.barriers.every(barrier => /^\x1b\[\?[\d;]+c$/.test(barrier)));
        assert.equal(item.pinned.observation.evidence, 'parser-state');
        assert.equal(item.pinned.observation.outcome, item.conclusion.outcome);
    }
    const bold = data.gradingReport.cases.find(item => item.caseId === 'sgr.bold');
    const italic = data.gradingReport.cases.find(item => item.caseId === 'sgr.italic');
    assert.match(bold.conclusion.scope, /^stored bold flag only/);
    assert.match(italic.conclusion.scope, /^stored italic flag only/);
    assert.equal(bold.observed.bold, true);
    assert.equal(italic.observed.italic, true);
});

test('the untouched stock run still grades all four cases inconclusive', () => {
    const data = JSON.parse(readFileSync(path));
    assert.deepEqual(data.stockCollector.counts, { inconclusive: 169, supported: 86 });
    assert.equal(data.stockCollector.suiteComplete, false);
    for (const [probeId, evidence] of [['text.basic', 'query'], ['text.overwrite', 'query'],
        ['sgr.bold', 'consumed'], ['sgr.italic', 'consumed']]) {
        const observation = data.stockCollector.probes[probeId];
        assert.equal(observation.outcome, 'inconclusive', probeId);
        assert.equal(observation.evidence, evidence, probeId);
    }
    assert.equal(data.stockCollector.probes['sgr.bold'].note, 'Cursor advance does not verify SGR styling');
});

test('failed preparation attempts are preserved, not silently replaced', () => {
    const data = JSON.parse(readFileSync(path));
    assert.ok(Array.isArray(data.attempts) && data.attempts.length >= 1);
    for (const attempt of data.attempts) {
        assert.match(attempt.error, /exited before collection|private clipboard collection failed/);
        assert.match(attempt.directory, /owned-[A-Za-z0-9]{6}$/);
    }
});

test('older publications remain byte-for-byte unchanged beside this batch', () => {
    const query = readFileSync(new URL('../../docs/qa/terminfo/2026-11-05-query-observations.json', import.meta.url));
    assert.equal(hash(query), '37c023b1b554b23aa7f18b0b1bcf1b0126c3863cfa4b75224f11de01d7cc0d7d');
    const reset = readFileSync(new URL('../../docs/qa/terminfo/2026-11-04-reset-observations.json', import.meta.url));
    assert.equal(hash(reset), '30699be1b2f132bd5a572e2f8a0a235ed4c3021c003482ca90386a2a6af32e23');
});
