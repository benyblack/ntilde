// Receipt binding for the xterm-color-stack qualification (modes.xtpushcolors and modes.xtpopcolors). The graded evidence stays provisional until the
// shared cleanup receipt exists, and the check re-derives the whole report from the retained paired journal, the worker's run log and the layered adapter bytes without the collector. The
// run carries no setting of its own. A run carrying this evidence must also carry every earlier grading layer (through left/right-margin).
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptGradingCollector } from './grading-collector.mjs';
import { layerChain } from './osc133-receipt.mjs';
import { xcsContractsVersion, xcsSuiteId } from './xcs-contracts.mjs';
import { adaptXcsCollector, xcsAdapterId } from './xcs-grading-collector.mjs';
import { buildXcsGradingReport } from './xcs-grading.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const artifact = Object.freeze({ report: 'xcs-observations.json', run: 'xcs-run.json', journal: 'xcs-snapshots.jsonl', adapter: 'collector-xcs.mjs' });
/** The first source policy whose worker always runs this suite. */
// PENDING: set to the source-policy number this batch takes once the device-query batch has merged and main's current policy is read.
export const xcsFirstPolicy = 56;
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const fail = message => { throw new Error(message); };
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;

/** Any retained xterm-color-stack artifact or declaration requires this suffix; a run from the first xterm-color-stack policy on always carries them. */
export function includesXcsEvidence(output, evidence, context) {
    const declared = ['xcsAdapterId', 'xcsSuiteId', 'xcsBundleSha256', 'xcsContractsVersion'].some(key => context[key] !== undefined);
    return policyNumber(context.sourceSnapshotPolicy) >= xcsFirstPolicy || declared || evidence.xcsEvidence !== undefined
        || Object.values(artifact).some(file => present(join(output, file)));
}

/** The outcome counts of the case conclusions (the pinned verdicts) and, separately, of the effect contract. */
export function xcsCounts(report) {
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return counts;
}

/** A retained artifact must be a regular file: a symlink could point at valid evidence that later disappears. */
function regularBytes(output, name) {
    if (!lstatSync(join(output, name)).isFile()) fail(`xterm-color-stack artifact is not a regular file: ${name}`);
    return readFileSync(join(output, name));
}

/** The retained extension must be exactly the layered derivation of every predecessor over the retained first grading extension over the owned adapter. */
function requireAdapter(output, binding, context) {
    const bytes = regularBytes(output, artifact.adapter);
    if (hash(bytes) !== binding.adapterBundleSha256 || context.xcsBundleSha256 !== binding.adapterBundleSha256) fail('xterm-color-stack adapter bytes changed.');
    let layer = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    for (const [name, adapt] of layerChain) {
        layer = Buffer.from(adapt(layer.toString()));
        if (hash(layer) !== context[`${name}BundleSha256`]) fail('xterm-color-stack adapter is not derived from the retained earlier grading layers.');
    }
    if (!Buffer.from(adaptXcsCollector(layer.toString())).equals(bytes)) fail('xterm-color-stack adapter is not the retained layered derivation.');
}

/** Full rederivation: paired journal, run log, layered adapter bytes and conclusions. */
export function checkXcsEvidence(output, evidence, runId, context) {
    if (context.xcsAdapterId !== xcsAdapterId || context.xcsSuiteId !== xcsSuiteId) fail('Missing or changed xterm-color-stack adapter declaration.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) fail('xterm-color-stack evidence requires every earlier grading layer.');
    const binding = evidence.xcsEvidence;
    if (binding === undefined) fail('Missing xterm-color-stack suite receipt binding.');
    const exact = binding && binding.runId === runId && binding.suiteId === xcsSuiteId && binding.file === artifact.report && binding.runFile === artifact.run
        && binding.provisionalUntil === 'cell-receipt.json' && binding.contractsVersion === xcsContractsVersion;
    if (!exact || !['sha256', 'runSha256', 'adapterBundleSha256'].every(key => isDigest(binding[key])) || context.xcsContractsVersion !== binding.contractsVersion) {
        fail('Invalid xterm-color-stack suite receipt binding.');
    }
    const reportBytes = regularBytes(output, binding.file), runBytes = regularBytes(output, binding.runFile), journalBytes = regularBytes(output, artifact.journal);
    if (hash(reportBytes) !== binding.sha256) fail('xterm-color-stack observation bytes changed.');
    if (hash(runBytes) !== binding.runSha256) fail('xterm-color-stack run log bytes changed.');
    requireAdapter(output, binding, context);
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes), journalText = journalBytes.toString('utf8').trim();
    if (![report.adapter.bundleSha256 === binding.adapterBundleSha256, report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256,
        report.contractsVersion === binding.contractsVersion].every(Boolean)) fail('xterm-color-stack report adapter or contract identity differs from the receipt.');
    if (!journalText) fail('xterm-color-stack journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildXcsGradingReport(frames, run, runId, report.adapter, { contractsVersion: report.contractsVersion });
    if (!sameJson(report, derived)) fail('xterm-color-stack observations do not match retained evidence.');
    if (!sameJson(frames, report.snapshots)) fail('xterm-color-stack journal does not match the report.');
    if (!sameJson(xcsCounts(report), binding.counts)) fail('xterm-color-stack receipt counts do not match the report.');
}

/** The case ids this suite grades, exposed for the receipt's planned-case declarations. */
export { xcsCaseIds } from './xcs-contracts.mjs';
