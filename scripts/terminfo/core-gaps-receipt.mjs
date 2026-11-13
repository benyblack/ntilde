import { readFileSync, lstatSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { coreGapsSuiteId, coreGapsAdapterId, adaptCoreGapsCollector, buildCoreGapsReport } from './core-gaps.mjs';
import { checkLrMarginsEvidence } from './lr-margins-receipt.mjs';

const files = ['cg-run.json', 'cg-observations.json', 'cg-snapshots.jsonl', 'collector-cg.mjs'];
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const check = condition => { if (!condition) throw new Error('Invalid Core-gap receipt binding.'); };
export function includesCoreGapsEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v35', 'readonly-owned-source-graph-v38', 'readonly-owned-source-graph-v39', 'readonly-owned-source-graph-v40', 'readonly-owned-source-graph-v41', 'readonly-owned-source-graph-v42', 'readonly-owned-source-graph-v43', 'readonly-owned-source-graph-v44', 'readonly-owned-source-graph-v45', 'readonly-owned-source-graph-v46', 'readonly-owned-source-graph-v47', 'readonly-owned-source-graph-v48', 'readonly-owned-source-graph-v49'].includes(context.sourceSnapshotPolicy) || context.coreGapsSuiteId !== undefined
        || evidence.coreGapsEvidence !== undefined || files.some(file => lstatSync(join(output, file), { throwIfNoEntry: false }));
}
export function checkCoreGapsEvidence(output, evidence, runId, context, proof) {
    checkLrMarginsEvidence(output, evidence, runId, context);
    const binding = evidence.coreGapsEvidence;
    check(context.coreGapsSuiteId === coreGapsSuiteId && context.coreGapsAdapterId === coreGapsAdapterId
        && binding?.runId === runId && binding.suiteId === coreGapsSuiteId && binding.file === 'cg-observations.json'
        && binding.runFile === 'cg-run.json' && binding.provisionalUntil === 'cell-receipt.json');
    for (const file of files) { const info = lstatSync(join(output, file)); check(info.isFile() && !info.isSymbolicLink()); }
    const reportBytes = readFileSync(join(output, binding.file)), runBytes = readFileSync(join(output, binding.runFile));
    const adapterBytes = readFileSync(join(output, 'collector-cg.mjs'));
    check(hash(reportBytes) === binding.sha256 && hash(runBytes) === binding.runSha256
        && hash(adapterBytes) === context.coreGapsBundleSha256 && hash(adapterBytes) === binding.adapterBundleSha256
        && adapterBytes.toString() === adaptCoreGapsCollector(readFileSync(join(output, 'collector-lrmm.mjs'))));
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'cg-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    check(report.adapter.bundleSha256 === hash(adapterBytes));
    check(JSON.stringify(buildCoreGapsReport(frames, run, runId, report.adapter)) === JSON.stringify(report));
    // The child that emitted TERM must be the independently proved App session worker.
    check(proof.proofs.some(entry => entry.worker?.pid === run.child.pid));
}
