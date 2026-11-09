// Receipt binding for the App-observed underline-color resolution suite. The evidence stays provisional until
// the shared cleanup receipt exists, and the check re-derives the whole report from retained bytes. There is no
// collector extension: the suite has no pinned probe. A run carrying it must also carry the pinned
// underline-color suite whose layered chain the shared finalizer re-derives; these conclusions sit beside it.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { underlineResolvedSuiteId, underlineResolvedContractsVersion } from './sgr-underline-color-resolved-contracts.mjs';
import { buildUnderlineResolvedReport } from './sgr-underline-color-resolved-grading.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['sgur-observations.json', 'sgur-run.json', 'sgur-snapshots.jsonl'];

function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained artifact or declaration requires the v22 suffix; a policy-v16 or v17 run always carries them. */
export function includesUnderlineResolvedEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v16', 'readonly-owned-source-graph-v17', 'readonly-owned-source-graph-v18', 'readonly-owned-source-graph-v19', 'readonly-owned-source-graph-v20', 'readonly-owned-source-graph-v21', 'readonly-owned-source-graph-v22', 'readonly-owned-source-graph-v23', 'readonly-owned-source-graph-v24', 'readonly-owned-source-graph-v25', 'readonly-owned-source-graph-v26', 'readonly-owned-source-graph-v27', 'readonly-owned-source-graph-v28', 'readonly-owned-source-graph-v29', 'readonly-owned-source-graph-v30', 'readonly-owned-source-graph-v31'].includes(context.sourceSnapshotPolicy)
        || context.underlineResolvedSuiteId !== undefined || context.underlineResolvedContractsVersion !== undefined
        || evidence.underlineResolvedEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Full rederivation: journal, run log and conclusions. */
export function checkUnderlineResolvedEvidence(output, evidence, runId, context) {
    if (context.underlineResolvedSuiteId !== underlineResolvedSuiteId) throw new Error('Missing or changed underline-resolved suite declaration.');
    if (context.sgrUnderlineColorAdapterId === undefined || evidence.sgrUnderlineColorEvidence === undefined) {
        throw new Error('Underline-resolved evidence requires its SGR-underline-color predecessor.');
    }
    const binding = evidence.underlineResolvedEvidence;
    if (binding === undefined) throw new Error('Missing underline-resolved suite receipt binding.');
    if (!binding || binding.runId !== runId || binding.suiteId !== underlineResolvedSuiteId
        || binding.file !== 'sgur-observations.json' || binding.runFile !== 'sgur-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || binding.contractsVersion !== underlineResolvedContractsVersion
        || context.underlineResolvedContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid underline-resolved suite receipt binding.');
    }
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('Underline-resolved observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('Underline-resolved run log bytes changed.');
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.contractsVersion !== binding.contractsVersion) throw new Error('Underline-resolved report contract identity differs from the receipt.');
    const journalText = readFileSync(join(output, 'sgur-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('Underline-resolved journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildUnderlineResolvedReport(frames, run, runId, { contractsVersion: report.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('Underline-resolved observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('Underline-resolved journal does not match the report.');
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    if (JSON.stringify(counts) !== JSON.stringify(binding.counts)) throw new Error('Underline-resolved receipt counts do not match the report.');
}
