// Receipt binding for the OSC 7/8 qualification (extensions.osc7-cwd and extensions.osc8). The graded evidence stays provisional until the shared cleanup receipt
// exists, and the check re-derives the whole report from the retained paired journal, the worker's pinned-run log and the layered adapter bytes without the
// collector. A run carrying this evidence must also carry every earlier grading layer (through left/right-margin): these conclusions sit beside theirs, never
// replace them.
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptGradingCollector } from './grading-collector.mjs';
import { layerChain } from './osc133-receipt.mjs';
import { osc78CaseIds, osc78ContractsVersion, osc78SuiteId } from './osc78-contracts.mjs';
import { adaptOsc78Collector, osc78AdapterId } from './osc78-grading-collector.mjs';
import { buildOsc78GradingReport, osc78Counts } from './osc78-grading.mjs';
export { osc78Counts };

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const artifact = Object.freeze({ report: 'lnk-observations.json', run: 'lnk-run.json', journal: 'lnk-snapshots.jsonl', marker: 'lnk-unavailable.json', adapter: 'collector-lnk.mjs' });
/** The first source policy whose worker always runs this suite. */
export const osc78FirstPolicy = 45;
/** The only typed refusals a first observation may end in: the owned metadata API or the fixture's grid is unavailable. */
const unavailableReasons = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);
const predecessors = 'every earlier grading layer (SGR through left/right-margin)';
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const fail = message => { throw new Error(message); };
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);
/** Whether a retained artifact path exists; any failure other than a missing entry is fatal. */
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;

/** Any retained OSC 7/8 artifact or declaration requires this suffix, and a run from the first OSC 7/8 policy on always carries them, because the current
 * worker runs this suite unconditionally after the Core reverse-video/wide-cell suite. */
export function includesOsc78Evidence(output, evidence, context) {
    const declared = ['osc78AdapterId', 'osc78SuiteId', 'osc78BundleSha256', 'osc78ContractsVersion'].some(key => context[key] !== undefined);
    return policyNumber(context.sourceSnapshotPolicy) >= osc78FirstPolicy || declared || evidence.osc78Evidence !== undefined
        || Object.values(artifact).some(file => present(join(output, file)));
}

/** The bytes of a retained artifact that must be a regular file: a symlink could point at valid evidence that later disappears. */
function regularBytes(output, name) {
    if (!lstatSync(join(output, name)).isFile()) fail(`OSC 7/8 artifact is not a regular file: ${name}`);
    return readFileSync(join(output, name));
}

/** The receipt binding must carry exactly the identity of this suite and run (and digests when it describes graded evidence). */
function requireBinding(evidence, expected, digests, message) {
    const binding = evidence.osc78Evidence;
    if (binding === undefined) fail('Missing OSC 7/8 suite receipt binding.');
    const exact = binding && Object.entries(expected).every(([key, value]) => sameJson(binding[key], value));
    if (!exact || !digests.every(key => isDigest(binding[key]))) fail(message);
    return binding;
}

/** The retained extension must be exactly the layered derivation of every predecessor extension over the retained first grading extension over the retained
 * owned adapter, and every predecessor bundle must be the one the run declared. */
function requireDerivation(output, adapterBytes, context) {
    let layer = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    for (const [name, adapt] of layerChain) {
        layer = Buffer.from(adapt(layer.toString()));
        if (hash(layer) !== context[`${name}BundleSha256`]) fail(`OSC 7/8 adapter is not derived from the retained ${predecessors}.`);
    }
    if (!Buffer.from(adaptOsc78Collector(layer.toString())).equals(adapterBytes)) fail('OSC 7/8 adapter is not the retained layered derivation.');
}

/** The adapter bytes must be the ones the receipt and the run's declarations bind. */
function requireAdapter(output, binding, context) {
    const bytes = regularBytes(output, artifact.adapter);
    if (hash(bytes) !== binding.adapterBundleSha256 || context.osc78BundleSha256 !== binding.adapterBundleSha256) fail('OSC 7/8 adapter bytes changed.');
    requireDerivation(output, bytes, context);
}

/** The owned metadata API (or the fixture's grid) was typed unavailable at the first observation: a marker, no run log and no report. */
function checkUnavailable(output, evidence, runId, context) {
    const binding = requireBinding(evidence, { runId, suiteId: osc78SuiteId, file: artifact.marker, status: 'unavailable', provisionalUntil: 'cell-receipt.json',
        contractsVersion: osc78ContractsVersion, counts: {} }, ['sha256', 'adapterBundleSha256'], 'Invalid OSC 7/8 unavailable receipt binding.');
    if (!unavailableReasons.has(binding.reason) || context.osc78ContractsVersion !== binding.contractsVersion) fail('Invalid OSC 7/8 unavailable receipt binding.');
    const graded = [artifact.report, artifact.run, artifact.journal].filter(file => present(join(output, file)) && readFileSync(join(output, file)).length > 0);
    if (graded.length > 0) fail('OSC 7/8 unavailable run retains graded artifacts.');
    const markerBytes = regularBytes(output, artifact.marker), marker = JSON.parse(markerBytes);
    const expected = { schemaVersion: 1, runId, suiteId: osc78SuiteId, status: 'unavailable', reason: binding.reason, plannedCases: osc78CaseIds.length };
    if (hash(markerBytes) !== binding.sha256 || !sameJson(marker, expected)) fail('OSC 7/8 unavailable marker changed.');
    requireAdapter(output, binding, context);
}

/** Full rederivation: paired journal, run log, layered adapter bytes and conclusions. */
export function checkOsc78Evidence(output, evidence, runId, context) {
    if (context.osc78AdapterId !== osc78AdapterId || context.osc78SuiteId !== osc78SuiteId) fail('Missing or changed OSC 7/8 adapter declaration.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) fail(`OSC 7/8 evidence requires ${predecessors}.`);
    if (evidence.osc78Evidence?.status === 'unavailable') return checkUnavailable(output, evidence, runId, context);
    const binding = requireBinding(evidence, { runId, suiteId: osc78SuiteId, file: artifact.report, runFile: artifact.run, provisionalUntil: 'cell-receipt.json',
        contractsVersion: osc78ContractsVersion }, ['sha256', 'runSha256', 'adapterBundleSha256'], 'Invalid OSC 7/8 suite receipt binding.');
    if (context.osc78ContractsVersion !== binding.contractsVersion) fail('Invalid OSC 7/8 suite receipt binding.');
    const reportBytes = regularBytes(output, binding.file), runBytes = regularBytes(output, binding.runFile), journalBytes = regularBytes(output, artifact.journal);
    if (hash(reportBytes) !== binding.sha256) fail('OSC 7/8 observation bytes changed.');
    if (hash(runBytes) !== binding.runSha256) fail('OSC 7/8 run log bytes changed.');
    requireAdapter(output, binding, context);
    const report = JSON.parse(reportBytes), journalText = journalBytes.toString('utf8').trim();
    const identity = [report.adapter.bundleSha256 === binding.adapterBundleSha256, report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256, report.contractsVersion === binding.contractsVersion];
    if (!identity.every(Boolean)) fail('OSC 7/8 report adapter or contract identity differs from the receipt.');
    if (!journalText) fail('OSC 7/8 journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildOsc78GradingReport(frames, JSON.parse(runBytes), runId, report.adapter, { contractsVersion: report.contractsVersion });
    if (!sameJson(report, derived)) fail('OSC 7/8 observations do not match retained evidence.');
    if (!sameJson(frames, report.snapshots)) fail('OSC 7/8 journal does not match the report.');
    if (!sameJson(osc78Counts(report), binding.counts)) fail('OSC 7/8 receipt counts do not match the report.');
}
