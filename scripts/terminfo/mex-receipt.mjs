// Receipt binding for the extended-mouse-encoding qualification (modes.utf8-mouse-1005 and input.urxvt-mouse). The graded evidence stays
// provisional until the shared cleanup receipt exists, and the check re-derives the whole report from the retained input journal,
// the worker's pinned-run log and the layered adapter bytes without the collector. A run carrying this evidence must also carry the
// owned routed-input suite evidence (the v4 plan keeps every earlier event verbatim) and every earlier grading layer: these conclusions sit
// beside theirs, never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { inputPlanForSuiteId, mexFirstPolicy } from './input-plan.mjs';
import { mexCaseIds, mexContractsVersion, mexGradedSuiteId } from './mex-contracts.mjs';
import { adaptMexCollector, mexAdapterId } from './mex-grading-collector.mjs';
import { buildMexGradingReport, mexCounts } from './mex-grading.mjs';
import { isDigest, layerChain, predecessorList, requireLayeredDerivation, requireLayeredSuiteBinding, readRetainedLayeredSuite, sameJson } from './routed-input-receipt.mjs';
export { mexCounts };
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns (the input journals belong to the owned input suite). */
const files = ['mex-observations.json', 'mex-run.json', 'mex-unavailable.json', 'collector-mex.mjs'];

/** Whether a retained artifact path exists; any failure other than a missing entry is fatal. */
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);
/** Any retained Extended-mouse artifact or declaration requires this suffix, and a run from the first extended-mouse policy on
 * always carries them, because the current worker runs the pinned probes unconditionally after the owned input suite. */
export function includesMexEvidence(output, evidence, context) {
    return policyNumber(context.sourceSnapshotPolicy) >= mexFirstPolicy
        || context.mexAdapterId !== undefined || context.mexSuiteId !== undefined
        || context.mexBundleSha256 !== undefined || context.mexContractsVersion !== undefined
        || evidence.mexEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

const jsonLines = (output, file) => readFileSync(join(output, file), 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line));

/** The owned input suite was typed unavailable (a grid below the plan's minimum): the layer carries the same marker, no run log and no report. */
function checkUnavailable(output, evidence, runId, context) {
    const binding = evidence.mexEvidence, input = evidence.inputEvidence;
    const identity = binding && binding.runId === runId && binding.suiteId === mexGradedSuiteId && binding.file === 'mex-unavailable.json'
        && binding.status === 'unavailable' && binding.reason === input.reason && binding.provisionalUntil === 'cell-receipt.json'
        && binding.contractsVersion === mexContractsVersion && context.mexContractsVersion === binding.contractsVersion
        && sameJson(binding.counts, {}) && isDigest(binding.sha256) && isDigest(binding.adapterBundleSha256);
    if (!identity) throw new Error('Invalid Extended-mouse unavailable receipt binding.');
    if (present(join(output, 'mex-observations.json')) || present(join(output, 'mex-run.json'))) throw new Error('Extended-mouse unavailable run retains graded artifacts.');
    for (const file of ['mex-unavailable.json', 'collector-mex.mjs']) if (!lstatSync(join(output, file)).isFile()) throw new Error(`Extended-mouse artifact is not a regular file: ${file}`);
    const markerBytes = readFileSync(join(output, 'mex-unavailable.json')), marker = JSON.parse(markerBytes);
    if (hash(markerBytes) !== binding.sha256 || marker.schemaVersion !== 1 || marker.runId !== runId || marker.suiteId !== mexGradedSuiteId
        || marker.status !== 'unavailable' || marker.reason !== input.reason || marker.plannedCases !== mexCaseIds.length) throw new Error('Extended-mouse unavailable marker changed.');
    const adapterBytes = readFileSync(join(output, 'collector-mex.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.mexBundleSha256 !== binding.adapterBundleSha256) throw new Error('Extended-mouse adapter bytes changed.');
    requireLayeredDerivation(output, adapterBytes, context, adaptMexCollector, 'Extended-mouse adapter');
}

/** Full rederivation: owned input journal, run log, layered adapter bytes and conclusions. */
export function checkMexEvidence(output, evidence, runId, context, proof) {
    const plan = inputPlanForSuiteId(context.inputSuiteId);
    if (context.mexAdapterId !== mexAdapterId || context.mexSuiteId !== mexGradedSuiteId) {
        throw new Error('Missing or changed Extended-mouse adapter declaration.');
    }
    if (plan === undefined || !['v4', 'v5', 'v6'].includes(plan.id)) throw new Error('Extended-mouse evidence requires the owned routed-input v4 or later suite plan.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) {
        throw new Error(`Extended-mouse evidence requires ${predecessorList}.`);
    }
    if (evidence.routedInputEvidence === undefined || context.routedInputAdapterId === undefined) {
        throw new Error('Extended-mouse evidence requires the routed-input qualification of the same journal.');
    }
    if (evidence.inputEvidence?.suiteId !== plan.suiteId || context.inputSuiteId !== plan.suiteId) {
        throw new Error('Extended-mouse evidence requires the owned routed-input suite of the run plan.');
    }
    if (evidence.inputEvidence.status === 'unavailable') return checkUnavailable(output, evidence, runId, context);
    if (evidence.inputEvidence.file !== 'input-observations.json') throw new Error('Extended-mouse evidence requires the completed owned input suite.');
    const binding = requireLayeredSuiteBinding(evidence.mexEvidence, runId, { suiteId: mexGradedSuiteId, file: 'mex-observations.json',
        runFile: 'mex-run.json', contractsVersion: mexContractsVersion, contextContractsVersion: context.mexContractsVersion,
        label: 'Extended-mouse' });
    const { bytes, runBytes, adapterBytes } = readRetainedLayeredSuite(output, binding, context, { regularFiles: ['mex-observations.json', 'mex-run.json',
        'collector-mex.mjs', 'input-snapshots.jsonl', 'input-actions.jsonl', 'input-observations.json'], collectorFile: 'collector-mex.mjs',
        contextBundleKey: 'mexBundleSha256', label: 'Extended-mouse' });
    requireLayeredDerivation(output, adapterBytes, context, adaptMexCollector, 'Extended-mouse adapter');
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    const identityMatches = [report.adapter.bundleSha256 === binding.adapterBundleSha256,
        report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256, report.contractsVersion === binding.contractsVersion,
        report.inputReportSha256 === evidence.inputEvidence.sha256];
    if (!identityMatches.every(Boolean)) throw new Error('Extended-mouse report adapter, contract or input identity differs from the receipt.');
    const input = { frames: jsonLines(output, 'input-snapshots.jsonl'), actions: jsonLines(output, 'input-actions.jsonl'), seal: proof?.proofs?.[0]?.input?.seal };
    const derived = buildMexGradingReport(input, run, runId, report.adapter, { contractsVersion: report.contractsVersion, inputPlan: plan,
        inputReportSha256: evidence.inputEvidence.sha256 });
    if (!sameJson(report, derived)) throw new Error('Extended-mouse observations do not match retained evidence.');
    if (!sameJson(mexCounts(report), binding.counts)) throw new Error('Extended-mouse receipt counts do not match the report.');
}
