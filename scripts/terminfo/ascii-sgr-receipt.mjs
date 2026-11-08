// Receipt binding for the ASCII/SGR qualification batch. The graded evidence
// stays provisional until the shared cleanup receipt exists, and the check
// re-derives the whole report from retained bytes without the collector.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { asciiSgrSuiteId, asciiSgrContractsVersion } from './ascii-sgr-contracts.mjs';
import { adaptGradingCollector, gradingAdapterId } from './grading-collector.mjs';
import { buildAsciiSgrGradingReport } from './ascii-sgr-grading.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const files = ['grading-observations.json', 'grading-run.json', 'grading-snapshots.jsonl', 'collector-grading.mjs'];

function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained grading artifact or declaration requires the v13 suffix —
 * and a policy-v6 run always carries them, because the current worker
 * collects grading unconditionally. The shared source graph retains the
 * modules in every run, so their presence in sources/ alone says nothing. */
export function includesAsciiSgrEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v6', 'readonly-owned-source-graph-v7', 'readonly-owned-source-graph-v8', 'readonly-owned-source-graph-v9', 'readonly-owned-source-graph-v10', 'readonly-owned-source-graph-v11', 'readonly-owned-source-graph-v12', 'readonly-owned-source-graph-v13', 'readonly-owned-source-graph-v14', 'readonly-owned-source-graph-v15', 'readonly-owned-source-graph-v16'].includes(context.sourceSnapshotPolicy)
        || context.gradingAdapterId !== undefined || context.asciiSgrSuiteId !== undefined
        || context.gradingBundleSha256 !== undefined || evidence.gradingEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Full rederivation: journal, run log, adapter bytes and conclusions. */
export function checkAsciiSgrEvidence(output, evidence, runId, context) {
    if (context.gradingAdapterId !== gradingAdapterId || context.asciiSgrSuiteId !== asciiSgrSuiteId) {
        throw new Error('Missing or changed grading adapter declaration.');
    }
    const binding = evidence.gradingEvidence;
    if (!binding || binding.runId !== runId || binding.suiteId !== asciiSgrSuiteId
        || binding.file !== 'grading-observations.json' || binding.runFile !== 'grading-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(binding.adapterBundleSha256 ?? '')
        || binding.contractsVersion !== asciiSgrContractsVersion
        || context.gradingContractsVersion !== asciiSgrContractsVersion) {
        throw new Error('Invalid graded suite receipt binding.');
    }
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('Graded observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('Graded run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-grading.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.gradingBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('Grading adapter bytes changed.');
    }
    // The retained extension must be exactly the derivation of the retained
    // owned adapter; matching declarations alone do not establish that.
    const derivedFromOwned = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    if (Buffer.compare(derivedFromOwned, adapterBytes) !== 0) {
        throw new Error('Grading adapter is not the retained owned-adapter derivation.');
    }
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256
        || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== asciiSgrContractsVersion) {
        throw new Error('Graded report adapter or contract identity differs from the receipt.');
    }
    const journalText = readFileSync(join(output, 'grading-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('Graded journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildAsciiSgrGradingReport(frames, run, runId, report.adapter);
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('Graded observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('Graded journal does not match the report.');
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    if (JSON.stringify(counts) !== JSON.stringify(binding.counts)) throw new Error('Graded receipt counts do not match the report.');
}
