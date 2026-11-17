import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { testWithPinnedCollector } from './real-bundle-test.mjs';
import { adaptCollector, upstreamBundleSha256 } from './collector.mjs';
import { adaptCellSizeCollector, cellSizeAdapterId, cellSizeSeams } from './cell-size-grading-collector.mjs';
import { cellSizeContext } from './cell-size-collector.mjs';
import { replayCellSizePinned, cellSizeSuiteId } from './cell-size-contracts.mjs';
import { buildCellSizeReport, cellSizeCounts } from './cell-size-grading.mjs';
import { checkCellSizeEvidence, includesCellSizeEvidence } from './cell-size-receipt.mjs';
import { syntheticCellSizeEvidence } from './fixtures/cell-size-evidence.mjs';

const hash = data => createHash('sha256').update(data).digest('hex');

test('pinned contexts refuse a changed request and a second query on either path', async () => {
    const report = { replies: [], barrier: '\x1b[?62c' };
    const termless = cellSizeContext(1, report);
    assert.throws(() => termless.feedCapture('\x1b]776;?\x07'), /request differs/);
    assert.equal(termless.feedCapture('\x1b]776\x07'), '');
    assert.throws(() => termless.feedCapture('\x1b]776\x07'), /request differs/);
    const terminal = cellSizeContext(1, report);
    await assert.rejects(terminal.queryWithSentinelOutcome('\x1b]776;?\x07', /x/), /request differs/);
    assert.deepEqual(await terminal.queryWithSentinelOutcome('\x1b]776\x07', /x/), { raw: report.barrier, match: null, reason: 'sentinel' });
    await assert.rejects(terminal.queryWithSentinelOutcome('\x1b]776\x07', /x/), /request differs/);
});

testWithPinnedCollector('real frozen OSC cell-size probes preserve their own criteria and the retained receipt refuses mutations', async bundleUrl => {
    const upstream = readFileSync(bundleUrl, 'utf8');
    const base = adaptCollector(upstream, fileURLToPath(new URL('./ownership.mjs', import.meta.url)));
    const source = adaptCellSizeCollector(base);
    assert.ok(source.startsWith(base));
    for (const [start] of cellSizeSeams) {
        assert.throws(() => adaptCellSizeCollector(base.replace(start, 'changed')), /seam changed/);
        assert.throws(() => adaptCellSizeCollector(base + start), /seam changed/);
    }
    assert.throws(() => adaptCellSizeCollector(base.replace('ReportCellSize=[1-9]', 'ReportCellSize=[0-9]')), /bytes changed/);
    const file = fileURLToPath(new URL('./ntilde-cell-size-test.mjs', bundleUrl));
    writeFileSync(file, source);
    const module = await import(pathToFileURL(file).href);
    const e = syntheticCellSizeEvidence();
    e.adapter = { id: cellSizeAdapterId, bundleSha256: hash(source), upstreamBundleSha256 };
    for (const [index, item] of e.run.cases.entries()) {
        const report = item.exchanges.find(value => value.id === 'pinned').report;
        for (const replies of [report.replies, [], index === 0 ? ['\x1b]1337;ReportCellSize=17.5;8.5\x07'] : ['\x1b]776;17;35;25\x07']]) {
            const exchange = { replies, barrier: report.barrier };
            const termless = module.ntildeRunCellSizeTermless(item.caseId, cellSizeContext(index, exchange));
            const terminal = await module.ntildeRunCellSizeTerminal(item.caseId, cellSizeContext(index, exchange));
            assert.deepEqual(termless, { probeId: item.caseId, ...replayCellSizePinned(index, replies.join('')) });
            assert.deepEqual(terminal, { probeId: item.caseId, ...replayCellSizePinned(index, replies.join('') + report.barrier) });
        }
    }
    const report = buildCellSizeReport(e.frames, e.run, e.runId, e.adapter, { replayModule: module });
    const root = mkdtempSync(join(tmpdir(), 'nt-cell-size-receipt-'));
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v63', cellSizeAdapterId,
        cellSizeSuiteId, cellSizeContractsVersion: 1, cellSizeBundleSha256: hash(source), adapterBundleSha256: hash(base), upstreamBundleSha256 };
    const runBytes = JSON.stringify(e.run), reportBytes = JSON.stringify(report);
    const binding = { runId: e.runId, suiteId: cellSizeSuiteId, contractsVersion: 1, file: 'cell-size-observations.json', runFile: 'cell-size-run.json',
        sha256: hash(reportBytes), runSha256: hash(runBytes), adapterBundleSha256: hash(source), counts: cellSizeCounts(report), provisionalUntil: 'cell-receipt.json' };
    const evidence = { cellSizeEvidence: binding };
    const files = { 'collector-adapter.mjs': base, 'collector-cell-size.mjs': source, 'cell-size-run.json': runBytes,
        'cell-size-observations.json': reportBytes, 'cell-size-snapshots.jsonl': e.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n' };
    try {
        assert.equal(includesCellSizeEvidence(root, {}, { sourceSnapshotPolicy: context.sourceSnapshotPolicy }), true);
        assert.equal(includesCellSizeEvidence(root, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v62' }), false);
        for (const [name, bytes] of Object.entries(files)) writeFileSync(join(root, name), bytes);
        assert.doesNotThrow(() => checkCellSizeEvidence(root, evidence, e.runId, context));
        assert.throws(() => checkCellSizeEvidence(root, {}, e.runId, context));
        assert.throws(() => checkCellSizeEvidence(root, evidence, 'b'.repeat(32), context));
        assert.throws(() => checkCellSizeEvidence(root, evidence, e.runId, { ...context, cellSizeSuiteId: 'other' }));
        assert.throws(() => checkCellSizeEvidence(root, { cellSizeEvidence: { ...binding, counts: { supported: 2 } } }, e.runId, context));
        for (const [name, bytes] of Object.entries(files)) {
            writeFileSync(join(root, name), 'changed');
            assert.throws(() => checkCellSizeEvidence(root, evidence, e.runId, context), name);
            writeFileSync(join(root, name), bytes);
        }
        const forged = structuredClone(report); forged.cases[0].effectContract.outcome = 'unsupported';
        const forgedBytes = JSON.stringify(forged); writeFileSync(join(root, binding.file), forgedBytes);
        assert.throws(() => checkCellSizeEvidence(root, { cellSizeEvidence: { ...binding, sha256: hash(forgedBytes) } }, e.runId, context));
    } finally { rmSync(root, { recursive: true, force: true }); }
});
