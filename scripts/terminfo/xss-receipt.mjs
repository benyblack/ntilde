// Receipt binding for the xterm-sgr-stack qualification (modes.xtpushsgr and modes.xtpopsgr). The graded evidence stays provisional until the
// shared cleanup receipt exists, and the check re-derives the whole report from the retained paired journal, the worker's run log and the layered adapter bytes without the collector. The
// run carries no setting of its own. A run carrying this evidence must also carry every earlier grading layer (through left/right-margin).
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptGradingCollector } from './grading-collector.mjs';
import { layerChain } from './osc133-receipt.mjs';
import { xssContractsVersion, xssSuiteId } from './xss-contracts.mjs';
import { adaptXssCollector, xssAdapterId } from './xss-grading-collector.mjs';
import { buildXssGradingReport } from './xss-grading.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const artifact = Object.freeze({ report: 'xss-observations.json', run: 'xss-run.json', journal: 'xss-snapshots.jsonl', adapter: 'collector-xss.mjs' });
/** The first source policy whose worker always runs this suite. */
export const xssFirstPolicy = 57;
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const fail = message => { throw new Error(message); };
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;

/** Any retained xterm-sgr-stack artifact or declaration requires this suffix; a run from the first xterm-sgr-stack policy on always carries them. */
export function includesXssEvidence(output, evidence, context) {
    const declared = ['xssAdapterId', 'xssSuiteId', 'xssBundleSha256', 'xssContractsVersion'].some(key => context[key] !== undefined);
    return policyNumber(context.sourceSnapshotPolicy) >= xssFirstPolicy || declared || evidence.xssEvidence !== undefined
        || Object.values(artifact).some(file => present(join(output, file)));
}

/** The outcome counts of the case conclusions (the pinned verdicts) and, separately, of the effect contract. */
export function xssCounts(report) {
    return report.cases.reduce((counts, item) => ({ ...counts, [item.conclusion.outcome]: (counts[item.conclusion.outcome] ?? 0) + 1 }), {});
}

/** A retained artifact must be a regular file: a symlink could point at valid evidence that later disappears. */
function regularBytes(output, name) {
    const path = join(output, name);
    if (!lstatSync(path).isFile()) fail(`xterm-sgr-stack artifact is not a regular file: ${name}`);
    return readFileSync(path);
}

/** The retained extension must be exactly the layered derivation of every predecessor over the retained first grading extension over the owned adapter. */
function requireAdapter(output, binding, context) {
    const bytes = regularBytes(output, artifact.adapter);
    if (hash(bytes) !== binding.adapterBundleSha256 || context.xssBundleSha256 !== binding.adapterBundleSha256) fail('xterm-sgr-stack adapter bytes changed.');
    let layer = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    for (const [name, adapt] of layerChain) {
        layer = Buffer.from(adapt(layer.toString()));
        if (hash(layer) !== context[`${name}BundleSha256`]) fail('xterm-sgr-stack adapter is not derived from the retained earlier grading layers.');
    }
    if (!Buffer.from(adaptXssCollector(layer.toString())).equals(bytes)) fail('xterm-sgr-stack adapter is not the retained layered derivation.');
}

/** Full rederivation: paired journal, run log, layered adapter bytes and conclusions. */
export function checkXssEvidence(output, evidence, runId, context) {
    if (context.xssAdapterId !== xssAdapterId || context.xssSuiteId !== xssSuiteId) fail('Missing or changed xterm-sgr-stack adapter declaration.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) fail('xterm-sgr-stack evidence requires every earlier grading layer.');
    const binding = evidence.xssEvidence;
    if (binding === undefined) fail('Missing xterm-sgr-stack suite receipt binding.');
    const exact = binding && binding.runId === runId && binding.suiteId === xssSuiteId && binding.file === artifact.report && binding.runFile === artifact.run
        && binding.provisionalUntil === 'cell-receipt.json' && binding.contractsVersion === xssContractsVersion;
    if (!exact || !['sha256', 'runSha256', 'adapterBundleSha256'].every(key => isDigest(binding[key])) || context.xssContractsVersion !== binding.contractsVersion) {
        fail('Invalid xterm-sgr-stack suite receipt binding.');
    }
    const reportBytes = regularBytes(output, binding.file), runBytes = regularBytes(output, binding.runFile), journalBytes = regularBytes(output, artifact.journal);
    if (hash(reportBytes) !== binding.sha256) fail('xterm-sgr-stack observation bytes changed.');
    if (hash(runBytes) !== binding.runSha256) fail('xterm-sgr-stack run log bytes changed.');
    requireAdapter(output, binding, context);
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes), journalText = journalBytes.toString('utf8').trim();
    if (![report.adapter.bundleSha256 === binding.adapterBundleSha256, report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256,
        report.contractsVersion === binding.contractsVersion].every(Boolean)) fail('xterm-sgr-stack report adapter or contract identity differs from the receipt.');
    if (!journalText) fail('xterm-sgr-stack journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildXssGradingReport(frames, run, runId, report.adapter, { contractsVersion: report.contractsVersion });
    if (!sameJson(report, derived)) fail('xterm-sgr-stack observations do not match retained evidence.');
    if (!sameJson(frames, report.snapshots)) fail('xterm-sgr-stack journal does not match the report.');
    if (!sameJson(xssCounts(report), binding.counts)) fail('xterm-sgr-stack receipt counts do not match the report.');
}

/** The case ids this suite grades, exposed for the receipt's planned-case declarations. */
export { xssCaseIds } from './xss-contracts.mjs';
