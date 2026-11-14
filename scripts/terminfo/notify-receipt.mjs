// Receipt binding for the OSC 777 / OSC 99 notification qualification (extensions.osc777-notify and extensions.osc99-kitty-notify). The graded evidence stays provisional until the
// shared cleanup receipt exists, and the check re-derives the whole report from the retained paired journal, the worker's run log, the parent's retained focus
// actions and the layered adapter bytes without the collector. A run carrying this evidence must also carry every earlier grading layer (through left/right-margin).
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptGradingCollector } from './grading-collector.mjs';
import { layerChain } from './osc133-receipt.mjs';
import { notifyCaseIds, notifyContractsVersion, notifySuiteId } from './notify-contracts.mjs';
import { adaptNotifyCollector, notifyAdapterId } from './notify-grading-collector.mjs';
import { buildNotifyReport, notifyCounts } from './notify-grading.mjs';
export { notifyCounts };

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const artifact = Object.freeze({ report: 'ntf-observations.json', run: 'ntf-run.json', journal: 'ntf-snapshots.jsonl', marker: 'ntf-unavailable.json', focus: 'ntf-focus.json', adapter: 'collector-ntf.mjs' });
/** The first source policy whose worker always runs this suite. */
export const notifyFirstPolicy = 53;
const unavailableReasons = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const fail = message => { throw new Error(message); };
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;

/** Any retained notification artifact or declaration requires this suffix; a run from the first notification policy on always carries them. */
export function includesNotifyEvidence(output, evidence, context) {
    const declared = ['notifyAdapterId', 'notifySuiteId', 'notifyBundleSha256', 'notifyContractsVersion'].some(key => context[key] !== undefined);
    return policyNumber(context.sourceSnapshotPolicy) >= notifyFirstPolicy || declared || evidence.notifyEvidence !== undefined
        || Object.values(artifact).some(file => present(join(output, file)));
}

/** A retained artifact must be a regular file: a symlink could point at valid evidence that later disappears. */
function regularBytes(output, name) {
    if (!lstatSync(join(output, name)).isFile()) fail(`notification artifact is not a regular file: ${name}`);
    return readFileSync(join(output, name));
}

function requireBinding(evidence, expected, digests, message) {
    const binding = evidence.notifyEvidence;
    if (binding === undefined) fail('Missing notification suite receipt binding.');
    const exact = binding && Object.entries(expected).every(([key, value]) => sameJson(binding[key], value));
    if (!exact || !digests.every(key => isDigest(binding[key]))) fail(message);
    return binding;
}

/** The retained extension must be exactly the layered derivation of every predecessor over the retained first grading extension over the owned adapter. */
function requireAdapter(output, binding, context) {
    const bytes = regularBytes(output, artifact.adapter);
    if (hash(bytes) !== binding.adapterBundleSha256 || context.notifyBundleSha256 !== binding.adapterBundleSha256) fail('notification adapter bytes changed.');
    let layer = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    for (const [name, adapt] of layerChain) {
        layer = Buffer.from(adapt(layer.toString()));
        if (hash(layer) !== context[`${name}BundleSha256`]) fail('notification adapter is not derived from the retained earlier grading layers.');
    }
    if (!Buffer.from(adaptNotifyCollector(layer.toString())).equals(bytes)) fail('notification adapter is not the retained layered derivation.');
}

function checkUnavailable(output, evidence, runId, context) {
    const binding = requireBinding(evidence, { runId, suiteId: notifySuiteId, file: artifact.marker, status: 'unavailable', provisionalUntil: 'cell-receipt.json',
        contractsVersion: notifyContractsVersion, counts: {} }, ['sha256', 'adapterBundleSha256'], 'Invalid notification unavailable receipt binding.');
    if (!unavailableReasons.has(binding.reason) || context.notifyContractsVersion !== binding.contractsVersion) fail('Invalid notification unavailable receipt binding.');
    const graded = [artifact.report, artifact.run, artifact.journal, artifact.focus].filter(file => present(join(output, file)) && readFileSync(join(output, file)).length > 0);
    if (graded.length > 0) fail('notification unavailable run retains graded artifacts.');
    const markerBytes = regularBytes(output, artifact.marker), expected = { schemaVersion: 1, runId, suiteId: notifySuiteId, status: 'unavailable', reason: binding.reason, plannedCases: notifyCaseIds.length };
    if (hash(markerBytes) !== binding.sha256 || !sameJson(JSON.parse(markerBytes), expected)) fail('notification unavailable marker changed.');
    requireAdapter(output, binding, context);
}

/** The parent's retained focus actions must be exactly the responses the worker journaled with the experiments that requested them. */
function requireFocusRecord(output, binding, run) {
    const bytes = regularBytes(output, artifact.focus);
    if (hash(bytes) !== binding.focusSha256) fail('notification focus record bytes changed.');
    // The owner socket adds its own transport fields (ok, ownerPid) to the parent's record.
    const journaled = run.experiments.flatMap(item => item.actions ?? []).map(({ response: { ok, ownerPid, ...record } }) => record);
    if (!sameJson(JSON.parse(bytes), journaled)) fail('notification focus record differs from the journaled actions.');
}

/** Full rederivation: paired journal, run log, focus record, layered adapter bytes and conclusions. */
export function checkNotifyEvidence(output, evidence, runId, context) {
    if (context.notifyAdapterId !== notifyAdapterId || context.notifySuiteId !== notifySuiteId) fail('Missing or changed notification adapter declaration.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) fail('notification evidence requires every earlier grading layer.');
    if (evidence.notifyEvidence?.status === 'unavailable') return checkUnavailable(output, evidence, runId, context);
    const binding = requireBinding(evidence, { runId, suiteId: notifySuiteId, file: artifact.report, runFile: artifact.run, provisionalUntil: 'cell-receipt.json',
        contractsVersion: notifyContractsVersion }, ['sha256', 'runSha256', 'adapterBundleSha256', 'focusSha256'], 'Invalid notification suite receipt binding.');
    if (context.notifyContractsVersion !== binding.contractsVersion) fail('Invalid notification suite receipt binding.');
    const reportBytes = regularBytes(output, binding.file), runBytes = regularBytes(output, binding.runFile), journalBytes = regularBytes(output, artifact.journal);
    if (hash(reportBytes) !== binding.sha256) fail('notification observation bytes changed.');
    if (hash(runBytes) !== binding.runSha256) fail('notification run log bytes changed.');
    requireAdapter(output, binding, context);
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes), journalText = journalBytes.toString('utf8').trim();
    if (![report.adapter.bundleSha256 === binding.adapterBundleSha256, report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256,
        report.contractsVersion === binding.contractsVersion].every(Boolean)) fail('notification report adapter or contract identity differs from the receipt.');
    if (!journalText) fail('notification journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    if (!sameJson(report, buildNotifyReport(frames, run, runId, report.adapter))) fail('notification observations do not match retained evidence.');
    if (!sameJson(frames, report.snapshots)) fail('notification journal does not match the report.');
    if (!sameJson(notifyCounts(report), binding.counts)) fail('notification receipt counts do not match the report.');
    requireFocusRecord(output, binding, run);
}
