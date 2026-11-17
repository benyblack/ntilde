// Receipt binding for the rxvt special-color qualification (extensions.osc5-special-color). The graded evidence stays provisional until the shared cleanup receipt exists, and the check re-derives the whole
// report from the retained paired journal, the worker's run log and the layered adapter bytes without the collector. The run carries no setting of its own. A run carrying this evidence must also carry
// every earlier grading layer (through left/right-margin).
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptGradingCollector } from './grading-collector.mjs';
import { layerChain } from './osc133-receipt.mjs';
import { osc5ContractsVersion, osc5SuiteId } from './osc5-contracts.mjs';
import { adaptOsc5Collector, osc5AdapterId } from './osc5-grading-collector.mjs';
import { buildOsc5GradingReport } from './osc5-grading.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const artifact = Object.freeze({ report: 'osc5-observations.json', run: 'osc5-run.json', journal: 'osc5-snapshots.jsonl', adapter: 'collector-osc5.mjs' });
/** The first source policy whose worker always runs this suite (the parent's plumbing sets the real number). */
export const osc5FirstPolicy = 63;
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const fail = message => { throw new Error(message); };
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;

/** Any retained special-color artifact or declaration requires this suffix; a run from the first special-color policy on always carries them. */
export function includesOsc5Evidence(output, evidence, context) {
    const declared = ['osc5AdapterId', 'osc5SuiteId', 'osc5BundleSha256', 'osc5ContractsVersion'].some(key => context[key] !== undefined);
    return policyNumber(context.sourceSnapshotPolicy) >= osc5FirstPolicy || declared || evidence.osc5Evidence !== undefined
        || Object.values(artifact).some(file => present(join(output, file)));
}

/** The outcome counts of the case conclusions (the pinned verdicts). */
export function osc5Counts(report) {
    return report.cases.reduce((counts, item) => ({ ...counts, [item.conclusion.outcome]: (counts[item.conclusion.outcome] ?? 0) + 1 }), {});
}

/** A retained artifact must be a regular file: a symlink could point at valid evidence that later disappears. */
function regularBytes(output, name) {
    const path = join(output, name);
    if (!lstatSync(path).isFile()) fail(`special-color artifact is not a regular file: ${name}`);
    return readFileSync(path);
}

/** The retained extension must be exactly the layered derivation of every predecessor over the retained first grading extension over the owned adapter. */
function requireAdapter(output, binding, context) {
    const bytes = regularBytes(output, artifact.adapter);
    if (hash(bytes) !== binding.adapterBundleSha256 || context.osc5BundleSha256 !== binding.adapterBundleSha256) fail('special-color adapter bytes changed.');
    let layer = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    for (const [name, adapt] of layerChain) {
        layer = Buffer.from(adapt(layer.toString()));
        if (hash(layer) !== context[`${name}BundleSha256`]) fail('special-color adapter is not derived from the retained earlier grading layers.');
    }
    if (!Buffer.from(adaptOsc5Collector(layer.toString())).equals(bytes)) fail('special-color adapter is not the retained layered derivation.');
}

/** The receipt binding of this suite, or a refusal when it is missing or does not match the run. */
function requireBinding(evidence, runId, context) {
    const binding = evidence.osc5Evidence;
    if (binding === undefined) fail('Missing special-color suite receipt binding.');
    const exact = binding && binding.runId === runId && binding.suiteId === osc5SuiteId && binding.file === artifact.report && binding.runFile === artifact.run
        && binding.provisionalUntil === 'cell-receipt.json' && binding.contractsVersion === osc5ContractsVersion;
    if (!exact || !['sha256', 'runSha256', 'adapterBundleSha256'].every(key => isDigest(binding[key])) || context.osc5ContractsVersion !== binding.contractsVersion) {
        fail('Invalid special-color suite receipt binding.');
    }
    return binding;
}

/** Full rederivation: paired journal, run log, layered adapter bytes and conclusions. */
export function checkOsc5Evidence(output, evidence, runId, context) {
    if (context.osc5AdapterId !== osc5AdapterId || context.osc5SuiteId !== osc5SuiteId) fail('Missing or changed special-color adapter declaration.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) fail('special-color evidence requires every earlier grading layer.');
    const binding = requireBinding(evidence, runId, context);
    const reportBytes = regularBytes(output, binding.file), runBytes = regularBytes(output, binding.runFile), journalBytes = regularBytes(output, artifact.journal);
    if (hash(reportBytes) !== binding.sha256) fail('special-color observation bytes changed.');
    if (hash(runBytes) !== binding.runSha256) fail('special-color run log bytes changed.');
    requireAdapter(output, binding, context);
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes), journalText = journalBytes.toString('utf8').trim();
    if (![report.adapter.bundleSha256 === binding.adapterBundleSha256, report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256,
        report.contractsVersion === binding.contractsVersion].every(Boolean)) fail('special-color report adapter or contract identity differs from the receipt.');
    if (!journalText) fail('special-color journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    if (!sameJson(report, buildOsc5GradingReport(frames, run, runId, report.adapter, { contractsVersion: report.contractsVersion }))) fail('special-color observations do not match retained evidence.');
    if (!sameJson(frames, report.snapshots)) fail('special-color journal does not match the report.');
    if (!sameJson(osc5Counts(report), binding.counts)) fail('special-color receipt counts do not match the report.');
}

/** The case ids this suite grades, exposed for the receipt's planned-case declarations. */
export { osc5CaseIds } from './osc5-contracts.mjs';
