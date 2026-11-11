// Receipt binding for the SGR reset/standard-color qualification batch. The
// graded evidence stays provisional until the shared cleanup receipt exists,
// and the check re-derives the whole report from retained bytes without the
// collector.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { sgrColorsSuiteId, sgrColorsContractsVersion } from './sgr-colors-contracts.mjs';
import { adaptSgrColorsCollector, sgrColorsAdapterId } from './sgr-colors-grading-collector.mjs';
import { adaptGradingCollector } from './grading-collector.mjs';
import { buildSgrColorsGradingReport } from './sgr-colors-grading.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const files = ['sgcolors-observations.json', 'sgcolors-run.json', 'sgcolors-snapshots.jsonl', 'collector-sgcolors.mjs'];

function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained SGR-colors artifact or declaration requires the v14 suffix —
 * and a policy-v7 run always carries them, because the current worker
 * collects this suite unconditionally after the first grading suite. */
export function includesSgrColorsEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v7', 'readonly-owned-source-graph-v8', 'readonly-owned-source-graph-v9', 'readonly-owned-source-graph-v10', 'readonly-owned-source-graph-v11', 'readonly-owned-source-graph-v12', 'readonly-owned-source-graph-v13', 'readonly-owned-source-graph-v14', 'readonly-owned-source-graph-v15', 'readonly-owned-source-graph-v16', 'readonly-owned-source-graph-v17', 'readonly-owned-source-graph-v18', 'readonly-owned-source-graph-v19', 'readonly-owned-source-graph-v20', 'readonly-owned-source-graph-v21', 'readonly-owned-source-graph-v22', 'readonly-owned-source-graph-v23', 'readonly-owned-source-graph-v24', 'readonly-owned-source-graph-v25', 'readonly-owned-source-graph-v26', 'readonly-owned-source-graph-v27', 'readonly-owned-source-graph-v28', 'readonly-owned-source-graph-v29', 'readonly-owned-source-graph-v30', 'readonly-owned-source-graph-v31', 'readonly-owned-source-graph-v32', 'readonly-owned-source-graph-v33', 'readonly-owned-source-graph-v34', 'readonly-owned-source-graph-v35', 'readonly-owned-source-graph-v36'].includes(context.sourceSnapshotPolicy)
        || context.sgrColorsAdapterId !== undefined || context.sgrColorsSuiteId !== undefined
        || context.sgrColorsBundleSha256 !== undefined || evidence.sgrColorsEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkSgrColorsEvidence(output, evidence, runId, context) {
    if (context.sgrColorsAdapterId !== sgrColorsAdapterId || context.sgrColorsSuiteId !== sgrColorsSuiteId) {
        throw new Error('Missing or changed SGR-colors adapter declaration.');
    }
    const binding = evidence.sgrColorsEvidence;
    if (!binding || binding.runId !== runId || binding.suiteId !== sgrColorsSuiteId
        || binding.file !== 'sgcolors-observations.json' || binding.runFile !== 'sgcolors-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(binding.adapterBundleSha256 ?? '')
        || ![1, 2].includes(binding.contractsVersion)
        || context.sgrColorsContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid SGR-colors suite receipt binding.');
    }
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('SGR-colors observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('SGR-colors run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-sgcolors.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.sgrColorsBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('SGR-colors adapter bytes changed.');
    }
    // The retained extension must be exactly the layered derivation of the
    // retained first grading extension over the retained owned adapter.
    const asciiSgrBytes = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    const derivedBytes = Buffer.from(adaptSgrColorsCollector(asciiSgrBytes.toString()));
    if (Buffer.compare(derivedBytes, adapterBytes) !== 0) {
        throw new Error('SGR-colors adapter is not the retained layered derivation.');
    }
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256
        || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || ![1, 2].includes(report.contractsVersion)
        || report.contractsVersion !== binding.contractsVersion) {
        throw new Error('SGR-colors report adapter or contract identity differs from the receipt.');
    }
    const journalText = readFileSync(join(output, 'sgcolors-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('SGR-colors journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildSgrColorsGradingReport(frames, run, runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('SGR-colors observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('SGR-colors journal does not match the report.');
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    if (JSON.stringify(counts) !== JSON.stringify(binding.counts)) throw new Error('SGR-colors receipt counts do not match the report.');
}
