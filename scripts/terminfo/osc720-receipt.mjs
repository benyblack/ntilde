// Receipt binding for the OSC 720 qualification (extensions.osc720-scroll-up). The graded evidence stays provisional until the
// shared cleanup receipt exists, and the check re-derives the whole report from the retained paired journal, the worker's run log, the parent's retained wheel
// actions and the layered adapter bytes without the collector. A run carrying this evidence must also carry every earlier grading layer (through left/right-margin).
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptGradingCollector } from './grading-collector.mjs';
import { layerChain } from './osc133-receipt.mjs';
import { osc720CaseIds, osc720ContractsVersion, osc720SuiteId } from './osc720-contracts.mjs';
import { adaptOsc720Collector, osc720AdapterId } from './osc720-grading-collector.mjs';
import { buildOsc720Report, osc720Counts } from './osc720-grading.mjs';
export { osc720Counts };

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const artifact = Object.freeze({ report: 'ovu-observations.json', run: 'ovu-run.json', journal: 'ovu-snapshots.jsonl', marker: 'ovu-unavailable.json', actions: 'ovu-actions.json', adapter: 'collector-ovu.mjs' });
/** The first source policy whose worker always runs this suite (the parent's plumbing sets the real number). */
export const osc720FirstPolicy = 67;
const unavailableReasons = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const fail = message => { throw new Error(message); };
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;

/** Any retained OSC 720 artifact or declaration requires this suffix; a run from the first OSC 720 policy on always carries them. */
export function includesOsc720Evidence(output, evidence, context) {
    const declared = ['osc720AdapterId', 'osc720SuiteId', 'osc720BundleSha256', 'osc720ContractsVersion'].some(key => context[key] !== undefined);
    return policyNumber(context.sourceSnapshotPolicy) >= osc720FirstPolicy || declared || evidence.osc720Evidence !== undefined
        || Object.values(artifact).some(file => present(join(output, file)));
}

/** A retained artifact must be a regular file: a symlink could point at valid evidence that later disappears. */
function regularBytes(output, name) {
    if (!lstatSync(join(output, name)).isFile()) fail(`OSC 720 artifact is not a regular file: ${name}`);
    return readFileSync(join(output, name));
}

function requireBinding(evidence, expected, digests, message) {
    const binding = evidence.osc720Evidence;
    if (binding === undefined) fail('Missing OSC 720 suite receipt binding.');
    const exact = binding && Object.entries(expected).every(([key, value]) => sameJson(binding[key], value));
    if (!exact || !digests.every(key => isDigest(binding[key]))) fail(message);
    return binding;
}

/** The retained extension must be exactly the layered derivation of every predecessor over the retained first grading extension over the owned adapter. */
function requireAdapter(output, binding, context) {
    const bytes = regularBytes(output, artifact.adapter);
    if (hash(bytes) !== binding.adapterBundleSha256 || context.osc720BundleSha256 !== binding.adapterBundleSha256) fail('OSC 720 adapter bytes changed.');
    let layer = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    for (const [name, adapt] of layerChain) {
        layer = Buffer.from(adapt(layer.toString()));
        if (hash(layer) !== context[`${name}BundleSha256`]) fail('OSC 720 adapter is not derived from the retained earlier grading layers.');
    }
    if (!Buffer.from(adaptOsc720Collector(layer.toString())).equals(bytes)) fail('OSC 720 adapter is not the retained layered derivation.');
}

function checkUnavailable(output, evidence, runId, context) {
    const binding = requireBinding(evidence, { runId, suiteId: osc720SuiteId, file: artifact.marker, status: 'unavailable', provisionalUntil: 'cell-receipt.json',
        contractsVersion: osc720ContractsVersion, counts: {} }, ['sha256', 'adapterBundleSha256'], 'Invalid OSC 720 unavailable receipt binding.');
    if (!unavailableReasons.has(binding.reason) || context.osc720ContractsVersion !== binding.contractsVersion) fail('Invalid OSC 720 unavailable receipt binding.');
    const graded = [artifact.report, artifact.run, artifact.journal, artifact.actions].filter(file => present(join(output, file)) && readFileSync(join(output, file)).length > 0);
    if (graded.length > 0) fail('OSC 720 unavailable run retains graded artifacts.');
    const markerBytes = regularBytes(output, artifact.marker), expected = { schemaVersion: 1, runId, suiteId: osc720SuiteId, status: 'unavailable', reason: binding.reason, plannedCases: osc720CaseIds.length };
    if (hash(markerBytes) !== binding.sha256 || !sameJson(JSON.parse(markerBytes), expected)) fail('OSC 720 unavailable marker changed.');
    requireAdapter(output, binding, context);
}

/** The parent's retained wheel actions must be exactly the responses the worker journaled with the experiments that requested them. */
function requireActionRecord(output, binding, run) {
    const bytes = regularBytes(output, artifact.actions);
    if (hash(bytes) !== binding.actionsSha256) fail('OSC 720 action record bytes changed.');
    // The owner socket adds its own transport fields (ok, ownerPid) to the parent's record.
    const journaled = run.experiments.flatMap(item => item.actions ?? []).map(({ response: { ok, ownerPid, ...record } }) => record);
    if (!sameJson(JSON.parse(bytes), journaled)) fail('OSC 720 action record differs from the journaled actions.');
}

/** Full rederivation: paired journal, run log, action record, layered adapter bytes and conclusions. */
export function checkOsc720Evidence(output, evidence, runId, context) {
    if (context.osc720AdapterId !== osc720AdapterId || context.osc720SuiteId !== osc720SuiteId) fail('Missing or changed OSC 720 adapter declaration.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) fail('OSC 720 evidence requires every earlier grading layer.');
    if (evidence.osc720Evidence?.status === 'unavailable') return checkUnavailable(output, evidence, runId, context);
    const binding = requireBinding(evidence, { runId, suiteId: osc720SuiteId, file: artifact.report, runFile: artifact.run, provisionalUntil: 'cell-receipt.json',
        contractsVersion: osc720ContractsVersion }, ['sha256', 'runSha256', 'adapterBundleSha256', 'actionsSha256'], 'Invalid OSC 720 suite receipt binding.');
    if (context.osc720ContractsVersion !== binding.contractsVersion) fail('Invalid OSC 720 suite receipt binding.');
    const reportBytes = regularBytes(output, binding.file), runBytes = regularBytes(output, binding.runFile), journalBytes = regularBytes(output, artifact.journal);
    if (hash(reportBytes) !== binding.sha256) fail('OSC 720 observation bytes changed.');
    if (hash(runBytes) !== binding.runSha256) fail('OSC 720 run log bytes changed.');
    requireAdapter(output, binding, context);
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes), journalText = journalBytes.toString('utf8').trim();
    if (![report.adapter.bundleSha256 === binding.adapterBundleSha256, report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256,
        report.contractsVersion === binding.contractsVersion].every(Boolean)) fail('OSC 720 report adapter or contract identity differs from the receipt.');
    if (!journalText) fail('OSC 720 journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    if (!sameJson(report, buildOsc720Report(frames, run, runId, report.adapter))) fail('OSC 720 observations do not match retained evidence.');
    if (!sameJson(frames, report.snapshots)) fail('OSC 720 journal does not match the report.');
    if (!sameJson(osc720Counts(report), binding.counts)) fail('OSC 720 receipt counts do not match the report.');
    requireActionRecord(output, binding, run);
}
