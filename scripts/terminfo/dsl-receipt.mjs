// Receipt binding for the DECSCLM qualification (modes.decsclm). The graded evidence stays provisional until the shared cleanup receipt exists, and the check re-derives
// the whole report from the retained paired journal, the worker's run log and the layered adapter bytes without the collector. The run carries no setting of its own. A run carrying this evidence must
// also carry every earlier grading layer (through left/right-margin).
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptGradingCollector } from './grading-collector.mjs';
import { layerChain } from './osc133-receipt.mjs';
import { dslContractsVersion, dslSuiteId } from './dsl-contracts.mjs';
import { adaptDslCollector, dslAdapterId } from './dsl-grading-collector.mjs';
import { buildDslGradingReport } from './dsl-grading.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const artifact = Object.freeze({ report: 'dsl-observations.json', run: 'dsl-run.json', journal: 'dsl-snapshots.jsonl', adapter: 'collector-dsl.mjs' });
/** The first source policy whose worker always runs this suite. This suite has no scoped-collection fork era: it was born on the integrated v78 graph (receipt schema 82); the focused v75-v77 forks never carried it. */
export const dslFirstPolicy = 78;
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const fail = message => { throw new Error(message); };
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;

/** Any retained DECSCLM artifact or declaration requires this suffix; a run from the first DECSCLM policy on always carries them. No earlier policy can hold this evidence: the suite
 *  was born on the integrated graph, with none of the scoped-collection fork eras the rxvt special-color receipt maps. */
export function includesDslEvidence(output, evidence, context) {
    const declared = ['dslAdapterId', 'dslSuiteId', 'dslBundleSha256', 'dslContractsVersion'].some(key => context[key] !== undefined);
    const required = policyNumber(context.sourceSnapshotPolicy) >= dslFirstPolicy;
    return required || declared || evidence.dslEvidence !== undefined
        || Object.values(artifact).some(file => present(join(output, file)));
}

/** The outcome counts of the case conclusions (the pinned verdicts). */
export function dslCounts(report) {
    return report.cases.reduce((counts, item) => ({ ...counts, [item.conclusion.outcome]: (counts[item.conclusion.outcome] ?? 0) + 1 }), {});
}

/** The retained bytes of one artifact: anything but a regular file (a symlink could point at valid evidence that later disappears) is a refusal. */
function retainedArtifact(output, name) {
    const path = join(output, name), stat = lstatSync(path);
    if (!stat.isFile()) fail(`DECSCLM artifact is not a regular file: ${name}`);
    return readFileSync(path);
}

/** The retained extension must be exactly the layered derivation of every predecessor over the retained first grading extension over the owned adapter. */
function requireAdapter(output, binding, context) {
    const bytes = retainedArtifact(output, artifact.adapter);
    if (hash(bytes) !== binding.adapterBundleSha256 || context.dslBundleSha256 !== binding.adapterBundleSha256) fail('DECSCLM adapter bytes changed.');
    let layer = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    for (const [name, adapt] of layerChain) {
        layer = Buffer.from(adapt(layer.toString()));
        if (hash(layer) !== context[`${name}BundleSha256`]) fail('DECSCLM adapter is not derived from the retained earlier grading layers.');
    }
    if (adaptDslCollector(layer.toString()) !== bytes.toString()) fail('DECSCLM adapter is not the retained layered derivation.');
}

/** The receipt binding of this suite, or a refusal when it is missing or does not match the run. */
function requireBinding(evidence, runId, context) {
    const binding = evidence.dslEvidence;
    if (binding === undefined) fail('Missing DECSCLM suite receipt binding.');
    const digests = ['sha256', 'runSha256', 'adapterBundleSha256'].every(key => isDigest(binding[key]));
    const shape = binding.runId === runId && binding.suiteId === dslSuiteId && binding.file === artifact.report && binding.runFile === artifact.run
        && binding.provisionalUntil === 'cell-receipt.json' && binding.contractsVersion === dslContractsVersion;
    if (!shape || !digests || context.dslContractsVersion !== binding.contractsVersion) fail('Invalid DECSCLM suite receipt binding.');
    return binding;
}

/** Full rederivation: paired journal, run log, layered adapter bytes and conclusions. */
export function checkDslEvidence(output, evidence, runId, context) {
    if (context.dslAdapterId !== dslAdapterId || context.dslSuiteId !== dslSuiteId) fail('Missing or changed DECSCLM adapter declaration.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) fail('DECSCLM evidence requires every earlier grading layer.');
    const binding = requireBinding(evidence, runId, context);
    const reportBytes = retainedArtifact(output, binding.file), runBytes = retainedArtifact(output, binding.runFile), journalBytes = retainedArtifact(output, artifact.journal);
    if (hash(reportBytes) !== binding.sha256) fail('DECSCLM observation bytes changed.');
    if (hash(runBytes) !== binding.runSha256) fail('DECSCLM run log bytes changed.');
    requireAdapter(output, binding, context);
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256 || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== binding.contractsVersion) fail('DECSCLM report adapter or contract identity differs from the receipt.');
    const journalText = journalBytes.toString('utf8').trim();
    if (journalText === '') fail('DECSCLM journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    if (!sameJson(report, buildDslGradingReport(frames, run, runId, report.adapter, { contractsVersion: report.contractsVersion }))) fail('DECSCLM observations do not match retained evidence.');
    if (!sameJson(frames, report.snapshots) || !sameJson(dslCounts(report), binding.counts)) fail('DECSCLM journal or receipt counts differ from the report.');
}

/** The case ids this suite grades, exposed for the receipt's planned-case declarations. */
export { dslCaseIds } from './dsl-contracts.mjs';
