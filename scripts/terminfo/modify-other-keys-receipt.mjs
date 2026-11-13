// Receipt binding for the modifyOtherKeys qualification (input.modify-other-keys, levels 1 and 2). The graded evidence stays
// provisional until the shared cleanup receipt exists, and the check re-derives the whole report from the retained input journal,
// the worker's pinned-run log and the layered adapter bytes without the collector. A run carrying this evidence must also carry the
// owned routed-input suite evidence (the v3 plan keeps its events verbatim) and every earlier grading layer: these conclusions sit
// beside theirs, never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { inputPlanForSuiteId, modifyOtherKeysFirstPolicy } from './input-plan.mjs';
import { modifyOtherKeysCaseIds, modifyOtherKeysContractsVersion, modifyOtherKeysGradedSuiteId } from './modify-other-keys-contracts.mjs';
import { adaptModifyOtherKeysCollector, modifyOtherKeysAdapterId } from './modify-other-keys-grading-collector.mjs';
import { buildModifyOtherKeysGradingReport, modifyOtherKeysCounts } from './modify-other-keys-grading.mjs';
import { isDigest, layerChain, predecessorList, requireLayeredDerivation, requireLayeredSuiteBinding, readRetainedLayeredSuite, sameJson } from './routed-input-receipt.mjs';
export { modifyOtherKeysCounts };
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns (the input journals belong to the owned input suite). */
const files = ['mok-observations.json', 'mok-run.json', 'mok-unavailable.json', 'collector-mok.mjs'];

/** Whether a retained artifact path exists; any failure other than a missing entry is fatal. */
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);
/** Any retained modifyOtherKeys artifact or declaration requires this suffix, and a run from the first modifyOtherKeys policy on
 * always carries them, because the current worker runs the pinned probe unconditionally after the owned input suite. */
export function includesModifyOtherKeysEvidence(output, evidence, context) {
    return policyNumber(context.sourceSnapshotPolicy) >= modifyOtherKeysFirstPolicy
        || context.modifyOtherKeysAdapterId !== undefined || context.modifyOtherKeysSuiteId !== undefined
        || context.modifyOtherKeysBundleSha256 !== undefined || context.modifyOtherKeysContractsVersion !== undefined
        || evidence.modifyOtherKeysEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

const jsonLines = (output, file) => readFileSync(join(output, file), 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line));

/** The owned input suite was typed unavailable (a grid below the plan's minimum): the layer carries the same marker, no run log and no report. */
function checkUnavailable(output, evidence, runId, context) {
    const binding = evidence.modifyOtherKeysEvidence, input = evidence.inputEvidence;
    const identity = binding && binding.runId === runId && binding.suiteId === modifyOtherKeysGradedSuiteId && binding.file === 'mok-unavailable.json'
        && binding.status === 'unavailable' && binding.reason === input.reason && binding.provisionalUntil === 'cell-receipt.json'
        && binding.contractsVersion === modifyOtherKeysContractsVersion && context.modifyOtherKeysContractsVersion === binding.contractsVersion
        && sameJson(binding.counts, {}) && isDigest(binding.sha256) && isDigest(binding.adapterBundleSha256);
    if (!identity) throw new Error('Invalid modifyOtherKeys unavailable receipt binding.');
    if (present(join(output, 'mok-observations.json')) || present(join(output, 'mok-run.json'))) throw new Error('modifyOtherKeys unavailable run retains graded artifacts.');
    for (const file of ['mok-unavailable.json', 'collector-mok.mjs']) if (!lstatSync(join(output, file)).isFile()) throw new Error(`modifyOtherKeys artifact is not a regular file: ${file}`);
    const markerBytes = readFileSync(join(output, 'mok-unavailable.json')), marker = JSON.parse(markerBytes);
    if (hash(markerBytes) !== binding.sha256 || marker.schemaVersion !== 1 || marker.runId !== runId || marker.suiteId !== modifyOtherKeysGradedSuiteId
        || marker.status !== 'unavailable' || marker.reason !== input.reason || marker.plannedCases !== modifyOtherKeysCaseIds.length) throw new Error('modifyOtherKeys unavailable marker changed.');
    const adapterBytes = readFileSync(join(output, 'collector-mok.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.modifyOtherKeysBundleSha256 !== binding.adapterBundleSha256) throw new Error('modifyOtherKeys adapter bytes changed.');
    requireLayeredDerivation(output, adapterBytes, context, adaptModifyOtherKeysCollector, 'modifyOtherKeys adapter');
}

/** Full rederivation: owned input journal, run log, layered adapter bytes and conclusions. */
export function checkModifyOtherKeysEvidence(output, evidence, runId, context, proof) {
    const plan = inputPlanForSuiteId(context.inputSuiteId);
    if (context.modifyOtherKeysAdapterId !== modifyOtherKeysAdapterId || context.modifyOtherKeysSuiteId !== modifyOtherKeysGradedSuiteId) {
        throw new Error('Missing or changed modifyOtherKeys adapter declaration.');
    }
    if (plan === undefined || plan.id !== 'v3') throw new Error('modifyOtherKeys evidence requires the owned routed-input v3 suite plan.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) {
        throw new Error(`modifyOtherKeys evidence requires ${predecessorList}.`);
    }
    if (evidence.routedInputEvidence === undefined || context.routedInputAdapterId === undefined) {
        throw new Error('modifyOtherKeys evidence requires the routed-input qualification of the same journal.');
    }
    if (evidence.inputEvidence?.suiteId !== plan.suiteId || context.inputSuiteId !== plan.suiteId) {
        throw new Error('modifyOtherKeys evidence requires the owned routed-input v3 suite.');
    }
    if (evidence.inputEvidence.status === 'unavailable') return checkUnavailable(output, evidence, runId, context);
    if (evidence.inputEvidence.file !== 'input-observations.json') throw new Error('modifyOtherKeys evidence requires the completed owned input suite.');
    const binding = requireLayeredSuiteBinding(evidence.modifyOtherKeysEvidence, runId, { suiteId: modifyOtherKeysGradedSuiteId, file: 'mok-observations.json',
        runFile: 'mok-run.json', contractsVersion: modifyOtherKeysContractsVersion, contextContractsVersion: context.modifyOtherKeysContractsVersion,
        label: 'modifyOtherKeys' });
    const { bytes, runBytes, adapterBytes } = readRetainedLayeredSuite(output, binding, context, { regularFiles: ['mok-observations.json', 'mok-run.json',
        'collector-mok.mjs', 'input-snapshots.jsonl', 'input-actions.jsonl', 'input-observations.json'], collectorFile: 'collector-mok.mjs',
        contextBundleKey: 'modifyOtherKeysBundleSha256', label: 'modifyOtherKeys' });
    requireLayeredDerivation(output, adapterBytes, context, adaptModifyOtherKeysCollector, 'modifyOtherKeys adapter');
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    const identityMatches = [report.adapter.bundleSha256 === binding.adapterBundleSha256,
        report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256, report.contractsVersion === binding.contractsVersion,
        report.inputReportSha256 === evidence.inputEvidence.sha256];
    if (!identityMatches.every(Boolean)) throw new Error('modifyOtherKeys report adapter, contract or input identity differs from the receipt.');
    const input = { frames: jsonLines(output, 'input-snapshots.jsonl'), actions: jsonLines(output, 'input-actions.jsonl'), seal: proof?.proofs?.[0]?.input?.seal };
    const derived = buildModifyOtherKeysGradingReport(input, run, runId, report.adapter, { contractsVersion: report.contractsVersion, inputPlan: plan,
        inputReportSha256: evidence.inputEvidence.sha256 });
    if (!sameJson(report, derived)) throw new Error('modifyOtherKeys observations do not match retained evidence.');
    if (!sameJson(modifyOtherKeysCounts(report), binding.counts)) throw new Error('modifyOtherKeys receipt counts do not match the report.');
}
