import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { charEditSeams } from './char-edit-grading-collector.mjs';
import { adaptInsertReplaceCollector, insertReplaceAdapterId } from './insert-replace-grading-collector.mjs';
import { insertReplaceContractsVersion, insertReplaceSuiteId } from './insert-replace-contracts.mjs';
import { insertReplacePinnedSeam } from './insert-replace-pinned.mjs';
import { buildInsertReplaceReport } from './insert-replace-grading.mjs';
import { checkInsertReplaceEvidence, includesInsertReplaceEvidence } from './insert-replace-receipt.mjs';
import { evidence as sample, runId } from './fixtures/insert-replace-grading-evidence.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
function fixture() {
    const output = mkdtempSync(join(tmpdir(), 'ntilde-irm-test-'));
    const write = (name, value) => writeFileSync(join(output, name), value);
    const upstream = [insertReplacePinnedSeam, ...charEditSeams.filter(seam => seam.startsWith('function isBlank(') || seam.startsWith('function parserStateResult(')),
        'export const ntildeLrMarginsAdapter = Object.freeze({', 'export function ntildeRunLrMarginsTermless(probeId, ctx) {'].join('\n');
    const adapter = adaptInsertReplaceCollector(upstream), { frames, run, adapter: identity } = sample();
    identity.bundleSha256 = hash(adapter);
    const report = buildInsertReplaceReport(frames, run, runId, identity);
    const reportBytes = JSON.stringify(report) + '\n', runBytes = JSON.stringify(run) + '\n';
    write('collector-lrmm.mjs', upstream); write('collector-irm.mjs', adapter); write('irm-observations.json', reportBytes); write('irm-run.json', runBytes);
    write('irm-snapshots.jsonl', frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v99', insertReplaceAdapterId, insertReplaceSuiteId,
        insertReplaceContractsVersion, insertReplaceBundleSha256: hash(adapter), lrMarginsBundleSha256: hash(upstream), upstreamBundleSha256: identity.upstreamBundleSha256 };
    const binding = { runId, suiteId: insertReplaceSuiteId, file: 'irm-observations.json', runFile: 'irm-run.json', provisionalUntil: 'cell-receipt.json',
        sha256: hash(reportBytes), runSha256: hash(runBytes), adapterBundleSha256: hash(adapter), contractsVersion: 1, counts: { inconclusive: 1 } };
    return { output, write, context, evidence: { lrMarginsEvidence: {}, insertReplaceEvidence: binding }, cleanup: () => rmSync(output, { recursive: true, force: true }) };
}

test('receipt rederives retained bytes and requires all artifacts for its policy', () => {
    const f = fixture();
    try {
        assert.equal(includesInsertReplaceEvidence(f.output, f.evidence, f.context, 99), true);
        assert.doesNotThrow(() => checkInsertReplaceEvidence(f.output, f.evidence, runId, f.context));
        f.write('irm-run.json', readFileSync(join(f.output, 'irm-run.json')) + ' ');
        assert.throws(() => checkInsertReplaceEvidence(f.output, f.evidence, runId, f.context), /retained bytes/);
    } finally { f.cleanup(); }
});

test('missing predecessor, cross-run substitution and tampered journal are refused', () => {
    for (const change of [f => { delete f.evidence.lrMarginsEvidence; }, f => { f.evidence.insertReplaceEvidence.runId = 'b'.repeat(32); },
        f => { f.write('irm-snapshots.jsonl', '{}\n'); }]) {
        const f = fixture();
        try { change(f); assert.throws(() => checkInsertReplaceEvidence(f.output, f.evidence, runId, f.context)); }
        finally { f.cleanup(); }
    }
});

test('the source policy requires the suffix even when every declaration and artifact is missing', () => {
    const output = mkdtempSync(join(tmpdir(), 'ntilde-irm-empty-'));
    try {
        assert.equal(includesInsertReplaceEvidence(output, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v99' }, 99), true);
        assert.throws(() => checkInsertReplaceEvidence(output, {}, runId, {}), /receipt binding/);
        assert.equal(includesInsertReplaceEvidence(output, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v98' }, 99), false);
    } finally { rmSync(output, { recursive: true, force: true }); }
});
