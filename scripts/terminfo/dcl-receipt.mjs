// Receipt binding for the DECCOLM qualification (modes.deccolm). The graded evidence stays provisional until the shared cleanup receipt exists, and the check re-derives
// the whole report from the retained paired journal, the worker's run log and the layered adapter bytes without the collector. The run carries no setting of its own. A run carrying this evidence must
// also carry every earlier grading layer (through left/right-margin).
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptGradingCollector } from './grading-collector.mjs';
import { layerChain } from './osc133-receipt.mjs';
import { dclContractsVersion, dclSuiteId } from './dcl-contracts.mjs';
import { adaptDclCollector, dclAdapterId } from './dcl-grading-collector.mjs';
import { buildDclGradingReport } from './dcl-grading.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const artifact = Object.freeze({ report: 'dcl-observations.json', run: 'dcl-run.json', journal: 'dcl-snapshots.jsonl', adapter: 'collector-dcl.mjs' });
/** The first source policy whose worker always runs this suite (the parent's plumbing sets the real number). This suite has no scoped-collection fork era: it was born on the integrated v72 graph. */
export const dclFirstPolicy = 72;
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const fail = message => { throw new Error(message); };
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;

/** Any retained DECCOLM artifact or declaration requires this suffix; a run from the first DECCOLM policy on always carries them. No earlier policy can hold this evidence: the suite
 *  was born on the integrated graph, with none of the scoped-collection fork eras the rxvt special-color receipt maps. */
export function includesDclEvidence(output, evidence, context) {
    const declared = ['dclAdapterId', 'dclSuiteId', 'dclBundleSha256', 'dclContractsVersion'].some(key => context[key] !== undefined);
    const required = policyNumber(context.sourceSnapshotPolicy) >= dclFirstPolicy;
    return required || declared || evidence.dclEvidence !== undefined
        || Object.values(artifact).some(file => present(join(output, file)));
}

/** The outcome counts of the case conclusions (the pinned verdicts). */
export function dclCounts(report) {
    return report.cases.reduce((counts, item) => ({ ...counts, [item.conclusion.outcome]: (counts[item.conclusion.outcome] ?? 0) + 1 }), {});
}

/** The retained bytes of one artifact: anything but a regular file (a symlink could point at valid evidence that later disappears) is a refusal. */
function retainedArtifact(output, name) {
    const path = join(output, name), stat = lstatSync(path);
    if (!stat.isFile()) fail(`DECCOLM artifact is not a regular file: ${name}`);
    return readFileSync(path);
}

/** The retained extension must be exactly the layered derivation of every predecessor over the retained first grading extension over the owned adapter. */
function requireAdapter(output, binding, context) {
    const bytes = retainedArtifact(output, artifact.adapter);
    if (hash(bytes) !== binding.adapterBundleSha256 || context.dclBundleSha256 !== binding.adapterBundleSha256) fail('DECCOLM adapter bytes changed.');
    let layer = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    for (const [name, adapt] of layerChain) {
        layer = Buffer.from(adapt(layer.toString()));
        if (hash(layer) !== context[`${name}BundleSha256`]) fail('DECCOLM adapter is not derived from the retained earlier grading layers.');
    }
    if (adaptDclCollector(layer.toString()) !== bytes.toString()) fail('DECCOLM adapter is not the retained layered derivation.');
}

/** The receipt binding of this suite, or a refusal when it is missing or does not match the run. */
function requireBinding(evidence, runId, context) {
    const binding = evidence.dclEvidence;
    if (binding === undefined) fail('Missing DECCOLM suite receipt binding.');
    const digests = ['sha256', 'runSha256', 'adapterBundleSha256'].every(key => isDigest(binding[key]));
    const shape = binding.runId === runId && binding.suiteId === dclSuiteId && binding.file === artifact.report && binding.runFile === artifact.run
        && binding.provisionalUntil === 'cell-receipt.json' && binding.contractsVersion === dclContractsVersion;
    if (!shape || !digests || context.dclContractsVersion !== binding.contractsVersion) fail('Invalid DECCOLM suite receipt binding.');
    return binding;
}

/** Full rederivation: paired journal, run log, layered adapter bytes and conclusions. */
export function checkDclEvidence(output, evidence, runId, context) {
    if (context.dclAdapterId !== dclAdapterId || context.dclSuiteId !== dclSuiteId) fail('Missing or changed DECCOLM adapter declaration.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) fail('DECCOLM evidence requires every earlier grading layer.');
    const binding = requireBinding(evidence, runId, context);
    const reportBytes = retainedArtifact(output, binding.file), runBytes = retainedArtifact(output, binding.runFile), journalBytes = retainedArtifact(output, artifact.journal);
    if (hash(reportBytes) !== binding.sha256) fail('DECCOLM observation bytes changed.');
    if (hash(runBytes) !== binding.runSha256) fail('DECCOLM run log bytes changed.');
    requireAdapter(output, binding, context);
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256 || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== binding.contractsVersion) fail('DECCOLM report adapter or contract identity differs from the receipt.');
    const journalText = journalBytes.toString('utf8').trim();
    if (journalText === '') fail('DECCOLM journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    if (!sameJson(report, buildDclGradingReport(frames, run, runId, report.adapter, { contractsVersion: report.contractsVersion }))) fail('DECCOLM observations do not match retained evidence.');
    if (!sameJson(frames, report.snapshots) || !sameJson(dclCounts(report), binding.counts)) fail('DECCOLM journal or receipt counts differ from the report.');
}

/** The case ids this suite grades, exposed for the receipt's planned-case declarations. */
export { dclCaseIds } from './dcl-contracts.mjs';
