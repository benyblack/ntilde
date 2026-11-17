// Receipt binding for the OSC 3008 qualification (extensions.osc3008-context). The graded evidence stays provisional until the shared cleanup receipt exists, and the check re-derives the whole
// report from the retained paired journal, the worker's run log and the layered adapter bytes without the collector. A run carrying this evidence must also carry every earlier grading layer
// (through left/right-margin). A typed initial refusal of the metadata API is the only admitted absence of a report.
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptGradingCollector } from './grading-collector.mjs';
import { layerChain } from './osc133-receipt.mjs';
import { osc3008CaseIds, osc3008ContractsVersion, osc3008SuiteId } from './osc3008-contracts.mjs';
import { adaptOsc3008Collector, osc3008AdapterId } from './osc3008-grading-collector.mjs';
import { buildOsc3008GradingReport, osc3008Counts } from './osc3008-grading.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const artifact = Object.freeze({ report: 'ocx-observations.json', run: 'ocx-run.json', journal: 'ocx-snapshots.jsonl', unavailable: 'ocx-unavailable.json', adapter: 'collector-ocx.mjs' });
/** The first source policy whose worker always runs this suite (the parent's plumbing sets the real number). */
export const osc3008FirstPolicy = 66;
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const fail = message => { throw new Error(message); };
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;
const unavailableReasons = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);

/** Any retained OSC 3008 artifact or declaration requires this suffix; a run from the first OSC 3008 policy on always carries them. */
export function includesOsc3008Evidence(output, evidence, context) {
    const declared = ['osc3008AdapterId', 'osc3008SuiteId', 'osc3008BundleSha256', 'osc3008ContractsVersion'].some(key => context[key] !== undefined);
    return policyNumber(context.sourceSnapshotPolicy) >= osc3008FirstPolicy || declared || evidence.osc3008Evidence !== undefined
        || Object.values(artifact).some(file => present(join(output, file)));
}

/** The retained bytes of one artifact: anything but a regular file (a symlink could point at valid evidence that later disappears) is a refusal. */
function retainedArtifact(output, name) {
    const path = join(output, name), stat = lstatSync(path);
    if (!stat.isFile()) fail(`OSC 3008 artifact is not a regular file: ${name}`);
    return readFileSync(path);
}

/** The retained extension must be exactly the layered derivation of every predecessor over the retained first grading extension over the owned adapter. */
function requireAdapter(output, binding, context) {
    const bytes = retainedArtifact(output, artifact.adapter);
    if (hash(bytes) !== binding.adapterBundleSha256 || context.osc3008BundleSha256 !== binding.adapterBundleSha256) fail('OSC 3008 adapter bytes changed.');
    let layer = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    for (const [name, adapt] of layerChain) {
        layer = Buffer.from(adapt(layer.toString()));
        if (hash(layer) !== context[`${name}BundleSha256`]) fail('OSC 3008 adapter is not derived from the retained earlier grading layers.');
    }
    if (adaptOsc3008Collector(layer.toString()) !== bytes.toString()) fail('OSC 3008 adapter is not the retained layered derivation.');
}

/** The receipt binding of this suite, or a refusal when it is missing or does not match the run. */
function requireBinding(evidence, runId, context) {
    const binding = evidence.osc3008Evidence;
    if (binding === undefined) fail('Missing OSC 3008 suite receipt binding.');
    const shape = binding.runId === runId && binding.suiteId === osc3008SuiteId && binding.provisionalUntil === 'cell-receipt.json'
        && binding.contractsVersion === osc3008ContractsVersion && context.osc3008ContractsVersion === binding.contractsVersion
        && isDigest(binding.sha256) && isDigest(binding.adapterBundleSha256);
    if (!shape) fail('Invalid OSC 3008 suite receipt binding.');
    return binding;
}

/** The metadata API (or the fixture's grid) was typed unavailable at the first observation: a marker, no run log and no report. */
function checkUnavailable(output, binding, runId) {
    if (binding.file !== artifact.unavailable || !unavailableReasons.has(binding.reason) || !sameJson(binding.counts, {})) fail('Invalid OSC 3008 unavailability.');
    for (const name of [artifact.report, artifact.run, artifact.journal]) {
        if (present(join(output, name)) && readFileSync(join(output, name)).length > 0) fail('Unavailable OSC 3008 retains graded artifacts.');
    }
    const bytes = retainedArtifact(output, artifact.unavailable), marker = JSON.parse(bytes);
    if (hash(bytes) !== binding.sha256 || marker.schemaVersion !== 1 || marker.runId !== runId || marker.suiteId !== osc3008SuiteId
        || marker.status !== 'unavailable' || marker.reason !== binding.reason || marker.plannedCases !== osc3008CaseIds.length) fail('OSC 3008 unavailable marker changed.');
}

/** Full rederivation: paired journal, run log, layered adapter bytes and conclusions. */
export function checkOsc3008Evidence(output, evidence, runId, context) {
    if (context.osc3008AdapterId !== osc3008AdapterId || context.osc3008SuiteId !== osc3008SuiteId) fail('Missing or changed OSC 3008 adapter declaration.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) fail('OSC 3008 evidence requires every earlier grading layer.');
    const binding = requireBinding(evidence, runId, context);
    requireAdapter(output, binding, context);
    if (binding.status === 'unavailable') { checkUnavailable(output, binding, runId); return; }
    if (binding.file !== artifact.report || binding.runFile !== artifact.run || !isDigest(binding.runSha256)) fail('Invalid OSC 3008 files.');
    const reportBytes = retainedArtifact(output, binding.file), runBytes = retainedArtifact(output, binding.runFile), journalBytes = retainedArtifact(output, artifact.journal);
    if (hash(reportBytes) !== binding.sha256 || hash(runBytes) !== binding.runSha256) fail('OSC 3008 retained bytes changed.');
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes), journalText = journalBytes.toString('utf8').trim();
    if (journalText === '') fail('OSC 3008 journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256 || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== binding.contractsVersion) fail('OSC 3008 report adapter or contract identity differs from the receipt.');
    if (!sameJson(report, buildOsc3008GradingReport(frames, run, runId, report.adapter))) fail('OSC 3008 observations do not match retained evidence.');
    if (!sameJson(frames, report.snapshots) || !sameJson(osc3008Counts(report), binding.counts)) fail('OSC 3008 journal or receipt counts differ from the report.');
}

/** The case ids this suite grades, exposed for the receipt's planned-case declarations. */
export { osc3008CaseIds } from './osc3008-contracts.mjs';
