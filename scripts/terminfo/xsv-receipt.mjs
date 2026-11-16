// Receipt binding for the xterm-saved-modes qualification (modes.xtsave and modes.xtrestore). The graded evidence stays provisional until the
// shared cleanup receipt exists, and the check re-derives the whole report from the retained paired journal, the worker's run log and the layered adapter bytes without the collector. The
// run carries no setting of its own. A run carrying this evidence must also carry every earlier grading layer (through left/right-margin).
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptGradingCollector } from './grading-collector.mjs';
import { layerChain } from './osc133-receipt.mjs';
import { xsvContractsVersion, xsvSuiteId } from './xsv-contracts.mjs';
import { adaptXsvCollector, xsvAdapterId } from './xsv-grading-collector.mjs';
import { buildXsvGradingReport } from './xsv-grading.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const artifact = Object.freeze({ report: 'xsv-observations.json', run: 'xsv-run.json', journal: 'xsv-snapshots.jsonl', adapter: 'collector-xsv.mjs' });
/** The first source policy whose worker always runs this suite. */
export const xsvFirstPolicy = 59;
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const fail = message => { throw new Error(message); };
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;

/** Any retained xterm-saved-modes artifact or declaration requires this suffix; a run from the first xterm-saved-modes policy on always carries them. */
export function includesXsvEvidence(output, evidence, context) {
    const declared = ['xsvAdapterId', 'xsvSuiteId', 'xsvBundleSha256', 'xsvContractsVersion'].some(key => context[key] !== undefined);
    return policyNumber(context.sourceSnapshotPolicy) >= xsvFirstPolicy || declared || evidence.xsvEvidence !== undefined
        || Object.values(artifact).some(file => present(join(output, file)));
}

/** The outcome counts of the case conclusions (the pinned verdicts) and, separately, of the effect contract. */
export function xsvCounts(report) {
    return report.cases.reduce((counts, item) => ({ ...counts, [item.conclusion.outcome]: (counts[item.conclusion.outcome] ?? 0) + 1 }), {});
}

/** A retained artifact must be a regular file: a symlink could point at valid evidence that later disappears. */
function regularBytes(output, name) {
    const path = join(output, name);
    if (!lstatSync(path).isFile()) fail(`xterm-saved-modes artifact is not a regular file: ${name}`);
    return readFileSync(path);
}

/** The retained extension must be exactly the layered derivation of every predecessor over the retained first grading extension over the owned adapter. */
function requireAdapter(output, binding, context) {
    const bytes = regularBytes(output, artifact.adapter);
    if (hash(bytes) !== binding.adapterBundleSha256 || context.xsvBundleSha256 !== binding.adapterBundleSha256) fail('xterm-saved-modes adapter bytes changed.');
    let layer = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    for (const [name, adapt] of layerChain) {
        layer = Buffer.from(adapt(layer.toString()));
        if (hash(layer) !== context[`${name}BundleSha256`]) fail('xterm-saved-modes adapter is not derived from the retained earlier grading layers.');
    }
    if (!Buffer.from(adaptXsvCollector(layer.toString())).equals(bytes)) fail('xterm-saved-modes adapter is not the retained layered derivation.');
}

/** Full rederivation: paired journal, run log, layered adapter bytes and conclusions. */
export function checkXsvEvidence(output, evidence, runId, context) {
    if (context.xsvAdapterId !== xsvAdapterId || context.xsvSuiteId !== xsvSuiteId) fail('Missing or changed xterm-saved-modes adapter declaration.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) fail('xterm-saved-modes evidence requires every earlier grading layer.');
    const binding = evidence.xsvEvidence;
    if (binding === undefined) fail('Missing xterm-saved-modes suite receipt binding.');
    const exact = binding && binding.runId === runId && binding.suiteId === xsvSuiteId && binding.file === artifact.report && binding.runFile === artifact.run
        && binding.provisionalUntil === 'cell-receipt.json' && binding.contractsVersion === xsvContractsVersion;
    if (!exact || !['sha256', 'runSha256', 'adapterBundleSha256'].every(key => isDigest(binding[key])) || context.xsvContractsVersion !== binding.contractsVersion) {
        fail('Invalid xterm-saved-modes suite receipt binding.');
    }
    const reportBytes = regularBytes(output, binding.file), runBytes = regularBytes(output, binding.runFile), journalBytes = regularBytes(output, artifact.journal);
    if (hash(reportBytes) !== binding.sha256) fail('xterm-saved-modes observation bytes changed.');
    if (hash(runBytes) !== binding.runSha256) fail('xterm-saved-modes run log bytes changed.');
    requireAdapter(output, binding, context);
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes), journalText = journalBytes.toString('utf8').trim();
    if (![report.adapter.bundleSha256 === binding.adapterBundleSha256, report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256,
        report.contractsVersion === binding.contractsVersion].every(Boolean)) fail('xterm-saved-modes report adapter or contract identity differs from the receipt.');
    if (!journalText) fail('xterm-saved-modes journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildXsvGradingReport(frames, run, runId, report.adapter, { contractsVersion: report.contractsVersion });
    if (!sameJson(report, derived)) fail('xterm-saved-modes observations do not match retained evidence.');
    if (!sameJson(frames, report.snapshots)) fail('xterm-saved-modes journal does not match the report.');
    if (!sameJson(xsvCounts(report), binding.counts)) fail('xterm-saved-modes receipt counts do not match the report.');
}

/** The case ids this suite grades, exposed for the receipt's planned-case declarations. */
export { xsvCaseIds } from './xsv-contracts.mjs';
