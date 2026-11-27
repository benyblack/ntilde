// Receipt binding for the SGR pixel-coordinate mouse qualification (input.pixel-mouse). The graded evidence stays
// provisional until the shared cleanup receipt exists, and the check re-derives the whole report from the retained input journal,
// the worker's pinned-run log and the layered adapter bytes without the collector. A run carrying this evidence must also carry the
// owned routed-input suite evidence (the v5 plan keeps every earlier event verbatim) and every earlier grading layer: these conclusions sit
// beside theirs, never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { inputPlanForSuiteId, pxmFirstPolicy } from './input-plan.mjs';
import { pxmCaseIds, pxmContractsVersion, pxmGradedSuiteId } from './pxm-contracts.mjs';
import { adaptPxmCollector, pxmAdapterId } from './pxm-grading-collector.mjs';
import { buildPxmGradingReport, pxmCounts } from './pxm-grading.mjs';
import { isDigest, layerChain, predecessorList, requireLayeredDerivation, requireLayeredSuiteBinding, readRetainedLayeredSuite, sameJson } from './routed-input-receipt.mjs';
export { pxmCounts };
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns (the input journals belong to the owned input suite). */
const files = ['pxm-observations.json', 'pxm-run.json', 'pxm-unavailable.json', 'collector-pxm.mjs'];

/** Whether a retained artifact path exists; any failure other than a missing entry is fatal. */
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);
/** Any retained SGR-pixel artifact or declaration requires this suffix, and a run from the first SGR-pixel policy on
 * always carries them, because the current worker runs the pinned probes unconditionally after the owned input suite. */
export function includesPxmEvidence(output, evidence, context) {
    return policyNumber(context.sourceSnapshotPolicy) >= pxmFirstPolicy
        || context.pxmAdapterId !== undefined || context.pxmSuiteId !== undefined
        || context.pxmBundleSha256 !== undefined || context.pxmContractsVersion !== undefined
        || evidence.pxmEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

const jsonLines = (output, file) => readFileSync(join(output, file), 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line));

/** The owned input suite was typed unavailable (a grid below the plan's minimum): the layer carries the same marker, no run log and no report. */
function checkUnavailable(output, evidence, runId, context) {
    const binding = evidence.pxmEvidence, input = evidence.inputEvidence;
    const identity = binding && binding.runId === runId && binding.suiteId === pxmGradedSuiteId && binding.file === 'pxm-unavailable.json'
        && binding.status === 'unavailable' && binding.reason === input.reason && binding.provisionalUntil === 'cell-receipt.json'
        && binding.contractsVersion === pxmContractsVersion && context.pxmContractsVersion === binding.contractsVersion
        && sameJson(binding.counts, {}) && isDigest(binding.sha256) && isDigest(binding.adapterBundleSha256);
    if (!identity) throw new Error('Invalid SGR-pixel unavailable receipt binding.');
    if (present(join(output, 'pxm-observations.json')) || present(join(output, 'pxm-run.json'))) throw new Error('SGR-pixel unavailable run retains graded artifacts.');
    for (const file of ['pxm-unavailable.json', 'collector-pxm.mjs']) if (!lstatSync(join(output, file)).isFile()) throw new Error(`SGR-pixel artifact is not a regular file: ${file}`);
    const markerBytes = readFileSync(join(output, 'pxm-unavailable.json')), marker = JSON.parse(markerBytes);
    if (hash(markerBytes) !== binding.sha256 || marker.schemaVersion !== 1 || marker.runId !== runId || marker.suiteId !== pxmGradedSuiteId
        || marker.status !== 'unavailable' || marker.reason !== input.reason || marker.plannedCases !== pxmCaseIds.length) throw new Error('SGR-pixel unavailable marker changed.');
    const adapterBytes = readFileSync(join(output, 'collector-pxm.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.pxmBundleSha256 !== binding.adapterBundleSha256) throw new Error('SGR-pixel adapter bytes changed.');
    requireLayeredDerivation(output, adapterBytes, context, adaptPxmCollector, 'SGR-pixel adapter');
}

/** Full rederivation: owned input journal, run log, layered adapter bytes and conclusions. */
export function checkPxmEvidence(output, evidence, runId, context, proof) {
    const plan = inputPlanForSuiteId(context.inputSuiteId);
    if (context.pxmAdapterId !== pxmAdapterId || context.pxmSuiteId !== pxmGradedSuiteId) {
        throw new Error('Missing or changed SGR-pixel adapter declaration.');
    }
    if (plan === undefined || !['v5', 'v6'].includes(plan.id)) throw new Error('SGR-pixel evidence requires the owned routed-input v5 or later suite plan.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) {
        throw new Error(`SGR-pixel evidence requires ${predecessorList}.`);
    }
    if (evidence.routedInputEvidence === undefined || context.routedInputAdapterId === undefined
        || evidence.mexEvidence === undefined || context.mexAdapterId === undefined) {
        throw new Error('SGR-pixel evidence requires the routed-input and extended-mouse qualifications of the same journal.');
    }
    if (evidence.inputEvidence?.suiteId !== plan.suiteId || context.inputSuiteId !== plan.suiteId) {
        throw new Error('SGR-pixel evidence requires the owned routed-input suite of the run plan.');
    }
    if (evidence.inputEvidence.status === 'unavailable') return checkUnavailable(output, evidence, runId, context);
    if (evidence.inputEvidence.file !== 'input-observations.json') throw new Error('SGR-pixel evidence requires the completed owned input suite.');
    const binding = requireLayeredSuiteBinding(evidence.pxmEvidence, runId, { suiteId: pxmGradedSuiteId, file: 'pxm-observations.json',
        runFile: 'pxm-run.json', contractsVersion: pxmContractsVersion, contextContractsVersion: context.pxmContractsVersion,
        label: 'SGR-pixel' });
    const { bytes, runBytes, adapterBytes } = readRetainedLayeredSuite(output, binding, context, { regularFiles: ['pxm-observations.json', 'pxm-run.json',
        'collector-pxm.mjs', 'input-snapshots.jsonl', 'input-actions.jsonl', 'input-observations.json'], collectorFile: 'collector-pxm.mjs',
        contextBundleKey: 'pxmBundleSha256', label: 'SGR-pixel' });
    requireLayeredDerivation(output, adapterBytes, context, adaptPxmCollector, 'SGR-pixel adapter');
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    const identityMatches = [report.adapter.bundleSha256 === binding.adapterBundleSha256,
        report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256, report.contractsVersion === binding.contractsVersion,
        report.inputReportSha256 === evidence.inputEvidence.sha256];
    if (!identityMatches.every(Boolean)) throw new Error('SGR-pixel report adapter, contract or input identity differs from the receipt.');
    const input = { frames: jsonLines(output, 'input-snapshots.jsonl'), actions: jsonLines(output, 'input-actions.jsonl'), seal: proof?.proofs?.[0]?.input?.seal };
    const derived = buildPxmGradingReport(input, run, runId, report.adapter, { contractsVersion: report.contractsVersion, inputPlan: plan,
        inputReportSha256: evidence.inputEvidence.sha256 });
    if (!sameJson(report, derived)) throw new Error('SGR-pixel observations do not match retained evidence.');
    if (!sameJson(pxmCounts(report), binding.counts)) throw new Error('SGR-pixel receipt counts do not match the report.');
}
