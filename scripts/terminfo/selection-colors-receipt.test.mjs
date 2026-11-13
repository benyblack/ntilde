import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { adaptSelectionColorsCollector, selectionColorsAdapterId, selectionColorsSeams } from './selection-colors-grading-collector.mjs';
import { checkSelectionColorsEvidence, includesSelectionColorsEvidence } from './selection-colors-receipt.mjs';
import { selectionColorsContractsVersion, selectionColorsSuiteId } from './selection-colors-contracts.mjs';
import { syntheticSelectionColorsEvidence, writeSelectionColorsPngs } from './fixtures/selection-colors-evidence.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const context = (over = {}) => ({ sourceSnapshotPolicy: 'readonly-owned-source-graph-v48', upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6',
    oscTitleBundleSha256: '1'.repeat(64), selectionColorsAdapterId, selectionColorsSuiteId, selectionColorsContractsVersion,
    selectionColorsBundleSha256: '2'.repeat(64), ...over });

test('the evidence is included when the policy, a context declaration, the summary evidence or a retained artifact says so', () => {
    const output = mkdtempSync(join(tmpdir(), 'selc-receipt-'));
    try {
        const minimal = over => context({ selectionColorsAdapterId: undefined, selectionColorsSuiteId: undefined,
            selectionColorsContractsVersion: undefined, selectionColorsBundleSha256: undefined, ...over });
        assert.equal(includesSelectionColorsEvidence(output, {}, context()), true, 'policy v48 or later always carries it');
        assert.equal(includesSelectionColorsEvidence(output, {}, minimal({ sourceSnapshotPolicy: 'readonly-owned-source-graph-v47' })), false);
        assert.equal(includesSelectionColorsEvidence(output, {}, minimal({ sourceSnapshotPolicy: 'readonly-owned-source-graph-v47', selectionColorsSuiteId })), true);
        assert.equal(includesSelectionColorsEvidence(output, { selectionColorsEvidence: {} }, minimal({ sourceSnapshotPolicy: 'readonly-owned-source-graph-v47' })), true);
        writeFileSync(join(output, 'selc-run.json'), '{}');
        assert.equal(includesSelectionColorsEvidence(output, {}, context({ sourceSnapshotPolicy: 'readonly-owned-source-graph-v1' })), true, 'a retained artifact includes it');
    } finally { rmSync(output, { recursive: true, force: true }); }
});

test('the receipt check rederives a complete synthetic run from its retained bytes and refuses every tampering', async () => {
    const output = mkdtempSync(join(tmpdir(), 'selc-receipt-'));
    try {
        // The retained predecessor layer carries every pinned seam of this derivation exactly once, as the real retained chain does.
        const previous = Buffer.from(selectionColorsSeams.join('\n') + '\n');
        const adapter = Buffer.from(adaptSelectionColorsCollector(previous));
        const evidence = syntheticSelectionColorsEvidence('a'.repeat(32), hash(adapter), {});
        writeFileSync(join(output, 'collector-ost.mjs'), previous, { mode: 0o600 });
        writeFileSync(join(output, 'collector-selc.mjs'), adapter, { mode: 0o600 });
        writeFileSync(join(output, 'selc-run.json'), JSON.stringify(evidence.run) + '\n', { mode: 0o600 });
        writeFileSync(join(output, 'selc-snapshots.jsonl'), evidence.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n', { mode: 0o600 });
        writeSelectionColorsPngs(evidence, output);
        const { buildSelectionColorsGradingReport } = await import('./selection-colors-grading.mjs');
        const report = buildSelectionColorsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { captureOf: evidence.captureOf });
        const binding = { runId: evidence.runId, suiteId: selectionColorsSuiteId, file: 'selc-observations.json', runFile: 'selc-run.json',
            provisionalUntil: 'cell-receipt.json', sha256: hash(JSON.stringify(report)), runSha256: hash(JSON.stringify(evidence.run) + '\n'),
            adapterBundleSha256: hash(adapter), contractsVersion: selectionColorsContractsVersion, counts: { supported: 4 } };
        writeFileSync(join(output, 'selc-observations.json'), JSON.stringify(report), { mode: 0o600 });
        const runContext = context({ oscTitleBundleSha256: hash(previous), selectionColorsBundleSha256: hash(adapter) });
        const evidenceBag = { colorResetEvidence: { runId: evidence.runId }, selectionColorsEvidence: binding };
        assert.doesNotThrow(() => checkSelectionColorsEvidence(output, evidenceBag, evidence.runId, runContext), 'a complete, consistent run rederives');

        const noPredecessor = { selectionColorsEvidence: binding };
        assert.throws(() => checkSelectionColorsEvidence(output, noPredecessor, evidence.runId, runContext), /receipt identity/, 'the runtime-color reset layer must be present');

        assert.throws(() => checkSelectionColorsEvidence(output, evidenceBag, 'b'.repeat(32), runContext), /identity/, 'a wrong run id is refused');

        const wrongCounts = { colorResetEvidence: { runId: evidence.runId }, selectionColorsEvidence: { ...binding, counts: { supported: 3 } } };
        assert.throws(() => checkSelectionColorsEvidence(output, wrongCounts, evidence.runId, runContext), /rederive|counts/, 'wrong counts are refused');

        const tamperedBytes = JSON.stringify({ ...report, cases: [] });
        writeFileSync(join(output, 'selc-observations.json'), tamperedBytes, { mode: 0o600 });
        const tamperedBag = { colorResetEvidence: { runId: evidence.runId }, selectionColorsEvidence: { ...binding, sha256: hash(tamperedBytes) } };
        assert.throws(() => checkSelectionColorsEvidence(output, tamperedBag, evidence.runId, runContext), /rederive/, 'tampered observations are refused');
        writeFileSync(join(output, 'selc-observations.json'), JSON.stringify(report), { mode: 0o600 });

        const wrongAdapter = context({ oscTitleBundleSha256: hash(previous), selectionColorsBundleSha256: '3'.repeat(64) });
        assert.throws(() => checkSelectionColorsEvidence(output, evidenceBag, evidence.runId, wrongAdapter), /adapter/, 'a changed adapter digest is refused');

        const unrelatedPrevious = Buffer.from('// something else\n');
        writeFileSync(join(output, 'collector-ost.mjs'), unrelatedPrevious, { mode: 0o600 });
        assert.throws(() => checkSelectionColorsEvidence(output, evidenceBag, evidence.runId, runContext), /derivation|digest/, 'a foreign predecessor layer is refused');
    } finally { rmSync(output, { recursive: true, force: true }); }
});
