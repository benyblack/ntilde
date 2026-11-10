// Receipt binding for the truecolor-literal finding suite. The finding evidence stays
// provisional until the shared cleanup receipt exists, and the check re-derives the whole
// report from retained bytes. There is no collector extension: the finding has no pinned
// probe. A run carrying it must also carry its truecolor predecessor, whose layered chain
// the shared finalizer re-derives; these conclusions sit beside theirs, never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { truecolorFindingSuiteId, truecolorFindingContractsVersion } from './truecolor-literal-finding-contracts.mjs';
import { buildTruecolorFindingReport } from './truecolor-literal-finding-grading.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['sgtf-observations.json', 'sgtf-run.json', 'sgtf-snapshots.jsonl'];

function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained finding artifact or declaration requires the v19 suffix, and a policy-v13 run
 * always carries them because the current worker collects the suite unconditionally. */
export function includesTruecolorFindingEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v13', 'readonly-owned-source-graph-v14', 'readonly-owned-source-graph-v15', 'readonly-owned-source-graph-v16', 'readonly-owned-source-graph-v17', 'readonly-owned-source-graph-v18', 'readonly-owned-source-graph-v19', 'readonly-owned-source-graph-v20', 'readonly-owned-source-graph-v21', 'readonly-owned-source-graph-v22', 'readonly-owned-source-graph-v23', 'readonly-owned-source-graph-v24', 'readonly-owned-source-graph-v25', 'readonly-owned-source-graph-v26', 'readonly-owned-source-graph-v27', 'readonly-owned-source-graph-v28', 'readonly-owned-source-graph-v29', 'readonly-owned-source-graph-v30', 'readonly-owned-source-graph-v31', 'readonly-owned-source-graph-v32', 'readonly-owned-source-graph-v33'].includes(context.sourceSnapshotPolicy)
        || context.truecolorFindingSuiteId !== undefined || context.truecolorFindingContractsVersion !== undefined
        || evidence.truecolorFindingEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Full rederivation: journal, run log and conclusions. */
export function checkTruecolorFindingEvidence(output, evidence, runId, context) {
    if (context.truecolorFindingSuiteId !== truecolorFindingSuiteId) throw new Error('Missing or changed truecolor-finding suite declaration.');
    if (context.sgrTruecolorAdapterId === undefined || evidence.sgrTruecolorEvidence === undefined) {
        throw new Error('Truecolor-finding evidence requires its SGR-truecolor predecessor.');
    }
    const binding = evidence.truecolorFindingEvidence;
    if (binding === undefined) throw new Error('Missing truecolor-finding suite receipt binding.');
    if (!binding || binding.runId !== runId || binding.suiteId !== truecolorFindingSuiteId
        || binding.file !== 'sgtf-observations.json' || binding.runFile !== 'sgtf-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || binding.contractsVersion !== truecolorFindingContractsVersion
        || context.truecolorFindingContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid truecolor-finding suite receipt binding.');
    }
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('Truecolor-finding observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('Truecolor-finding run log bytes changed.');
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.contractsVersion !== binding.contractsVersion) throw new Error('Truecolor-finding report contract identity differs from the receipt.');
    const journalText = readFileSync(join(output, 'sgtf-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('Truecolor-finding journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildTruecolorFindingReport(frames, run, runId, { contractsVersion: report.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('Truecolor-finding observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('Truecolor-finding journal does not match the report.');
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    if (JSON.stringify(counts) !== JSON.stringify(binding.counts)) throw new Error('Truecolor-finding receipt counts do not match the report.');
}
