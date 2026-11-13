// Receipt binding for the OSC 9 qualification (extensions.osc9-progress and extensions.notifications). The graded evidence stays provisional until the
// shared cleanup receipt exists, and the check re-derives the whole report from the retained paired journal, the worker's run log, the parent's retained focus
// actions and the layered adapter bytes without the collector. A run carrying this evidence must also carry every earlier grading layer (through left/right-margin).
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptGradingCollector } from './grading-collector.mjs';
import { layerChain } from './osc133-receipt.mjs';
import { osc9CaseIds, osc9ContractsVersion, osc9SuiteId } from './osc9-contracts.mjs';
import { adaptOsc9Collector, osc9AdapterId } from './osc9-grading-collector.mjs';
import { buildOsc9Report, osc9Counts } from './osc9-grading.mjs';
export { osc9Counts };

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const artifact = Object.freeze({ report: 'osn-observations.json', run: 'osn-run.json', journal: 'osn-snapshots.jsonl', marker: 'osn-unavailable.json', focus: 'osn-focus.json', adapter: 'collector-osn.mjs' });
/** The first source policy whose worker always runs this suite. */
export const osc9FirstPolicy = 50;
const unavailableReasons = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const fail = message => { throw new Error(message); };
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;

/** Any retained OSC 9 artifact or declaration requires this suffix; a run from the first OSC 9 policy on always carries them. */
export function includesOsc9Evidence(output, evidence, context) {
    const declared = ['osc9AdapterId', 'osc9SuiteId', 'osc9BundleSha256', 'osc9ContractsVersion'].some(key => context[key] !== undefined);
    return policyNumber(context.sourceSnapshotPolicy) >= osc9FirstPolicy || declared || evidence.osc9Evidence !== undefined
        || Object.values(artifact).some(file => present(join(output, file)));
}

/** A retained artifact must be a regular file: a symlink could point at valid evidence that later disappears. */
function regularBytes(output, name) {
    if (!lstatSync(join(output, name)).isFile()) fail(`OSC 9 artifact is not a regular file: ${name}`);
    return readFileSync(join(output, name));
}

function requireBinding(evidence, expected, digests, message) {
    const binding = evidence.osc9Evidence;
    if (binding === undefined) fail('Missing OSC 9 suite receipt binding.');
    const exact = binding && Object.entries(expected).every(([key, value]) => sameJson(binding[key], value));
    if (!exact || !digests.every(key => isDigest(binding[key]))) fail(message);
    return binding;
}

/** The retained extension must be exactly the layered derivation of every predecessor over the retained first grading extension over the owned adapter. */
function requireAdapter(output, binding, context) {
    const bytes = regularBytes(output, artifact.adapter);
    if (hash(bytes) !== binding.adapterBundleSha256 || context.osc9BundleSha256 !== binding.adapterBundleSha256) fail('OSC 9 adapter bytes changed.');
    let layer = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    for (const [name, adapt] of layerChain) {
        layer = Buffer.from(adapt(layer.toString()));
        if (hash(layer) !== context[`${name}BundleSha256`]) fail('OSC 9 adapter is not derived from the retained earlier grading layers.');
    }
    if (!Buffer.from(adaptOsc9Collector(layer.toString())).equals(bytes)) fail('OSC 9 adapter is not the retained layered derivation.');
}

function checkUnavailable(output, evidence, runId, context) {
    const binding = requireBinding(evidence, { runId, suiteId: osc9SuiteId, file: artifact.marker, status: 'unavailable', provisionalUntil: 'cell-receipt.json',
        contractsVersion: osc9ContractsVersion, counts: {} }, ['sha256', 'adapterBundleSha256'], 'Invalid OSC 9 unavailable receipt binding.');
    if (!unavailableReasons.has(binding.reason) || context.osc9ContractsVersion !== binding.contractsVersion) fail('Invalid OSC 9 unavailable receipt binding.');
    const graded = [artifact.report, artifact.run, artifact.journal, artifact.focus].filter(file => present(join(output, file)) && readFileSync(join(output, file)).length > 0);
    if (graded.length > 0) fail('OSC 9 unavailable run retains graded artifacts.');
    const markerBytes = regularBytes(output, artifact.marker), expected = { schemaVersion: 1, runId, suiteId: osc9SuiteId, status: 'unavailable', reason: binding.reason, plannedCases: osc9CaseIds.length };
    if (hash(markerBytes) !== binding.sha256 || !sameJson(JSON.parse(markerBytes), expected)) fail('OSC 9 unavailable marker changed.');
    requireAdapter(output, binding, context);
}

/** The parent's retained focus actions must be exactly the responses the worker journaled with the experiments that requested them. */
function requireFocusRecord(output, binding, run) {
    const bytes = regularBytes(output, artifact.focus);
    if (hash(bytes) !== binding.focusSha256) fail('OSC 9 focus record bytes changed.');
    const journaled = run.experiments.flatMap(item => item.actions ?? []).map(item => item.response);
    if (!sameJson(JSON.parse(bytes), journaled)) fail('OSC 9 focus record differs from the journaled actions.');
}

/** Full rederivation: paired journal, run log, focus record, layered adapter bytes and conclusions. */
export function checkOsc9Evidence(output, evidence, runId, context) {
    if (context.osc9AdapterId !== osc9AdapterId || context.osc9SuiteId !== osc9SuiteId) fail('Missing or changed OSC 9 adapter declaration.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) fail('OSC 9 evidence requires every earlier grading layer.');
    if (evidence.osc9Evidence?.status === 'unavailable') return checkUnavailable(output, evidence, runId, context);
    const binding = requireBinding(evidence, { runId, suiteId: osc9SuiteId, file: artifact.report, runFile: artifact.run, provisionalUntil: 'cell-receipt.json',
        contractsVersion: osc9ContractsVersion }, ['sha256', 'runSha256', 'adapterBundleSha256', 'focusSha256'], 'Invalid OSC 9 suite receipt binding.');
    if (context.osc9ContractsVersion !== binding.contractsVersion) fail('Invalid OSC 9 suite receipt binding.');
    const reportBytes = regularBytes(output, binding.file), runBytes = regularBytes(output, binding.runFile), journalBytes = regularBytes(output, artifact.journal);
    if (hash(reportBytes) !== binding.sha256) fail('OSC 9 observation bytes changed.');
    if (hash(runBytes) !== binding.runSha256) fail('OSC 9 run log bytes changed.');
    requireAdapter(output, binding, context);
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes), journalText = journalBytes.toString('utf8').trim();
    if (![report.adapter.bundleSha256 === binding.adapterBundleSha256, report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256,
        report.contractsVersion === binding.contractsVersion].every(Boolean)) fail('OSC 9 report adapter or contract identity differs from the receipt.');
    if (!journalText) fail('OSC 9 journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    if (!sameJson(report, buildOsc9Report(frames, run, runId, report.adapter))) fail('OSC 9 observations do not match retained evidence.');
    if (!sameJson(frames, report.snapshots)) fail('OSC 9 journal does not match the report.');
    if (!sameJson(osc9Counts(report), binding.counts)) fail('OSC 9 receipt counts do not match the report.');
    requireFocusRecord(output, binding, run);
}
