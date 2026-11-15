// Receipt binding for the device-query qualification (device.xtgetxres, device.xtreportcolors, device.xtwinops-20 and device.xtwinops-21). The graded evidence stays provisional until the
// shared cleanup receipt exists, and the check re-derives the whole report from the retained paired journal, the worker's run log and the layered adapter bytes without the collector. The
// run must have turned on AllowTitleReport (off by default) in its retained initial settings. A run carrying this evidence must also carry every earlier grading layer (through left/right-margin).
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptGradingCollector } from './grading-collector.mjs';
import { layerChain } from './osc133-receipt.mjs';
import { devqContractsVersion, devqSuiteId } from './devq-contracts.mjs';
import { adaptDevqCollector, devqAdapterId } from './devq-grading-collector.mjs';
import { buildDevqGradingReport } from './devq-grading.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const artifact = Object.freeze({ report: 'dvq-observations.json', run: 'dvq-run.json', journal: 'dvq-snapshots.jsonl', adapter: 'collector-dvq.mjs' });
/** The first source policy whose worker always runs this suite. */
export const devqFirstPolicy = 54;
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const fail = message => { throw new Error(message); };
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;

/** Any retained device-query artifact or declaration requires this suffix; a run from the first device-query policy on always carries them. */
export function includesDevqEvidence(output, evidence, context) {
    const declared = ['devqAdapterId', 'devqSuiteId', 'devqBundleSha256', 'devqContractsVersion'].some(key => context[key] !== undefined);
    return policyNumber(context.sourceSnapshotPolicy) >= devqFirstPolicy || declared || evidence.devqEvidence !== undefined
        || Object.values(artifact).some(file => present(join(output, file)));
}

/** The outcome counts of the case conclusions (the pinned verdicts) and, separately, of the effect contract. */
export function devqCounts(report) {
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return counts;
}

/** A retained artifact must be a regular file: a symlink could point at valid evidence that later disappears. */
function regularBytes(output, name) {
    if (!lstatSync(join(output, name)).isFile()) fail(`device-query artifact is not a regular file: ${name}`);
    return readFileSync(join(output, name));
}

/** The retained extension must be exactly the layered derivation of every predecessor over the retained first grading extension over the owned adapter. */
function requireAdapter(output, binding, context) {
    const bytes = regularBytes(output, artifact.adapter);
    if (hash(bytes) !== binding.adapterBundleSha256 || context.devqBundleSha256 !== binding.adapterBundleSha256) fail('device-query adapter bytes changed.');
    let layer = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    for (const [name, adapt] of layerChain) {
        layer = Buffer.from(adapt(layer.toString()));
        if (hash(layer) !== context[`${name}BundleSha256`]) fail('device-query adapter is not derived from the retained earlier grading layers.');
    }
    if (!Buffer.from(adaptDevqCollector(layer.toString())).equals(bytes)) fail('device-query adapter is not the retained layered derivation.');
}

/** The retained initial settings must turn the off-by-default title reports on, and the run must declare the same digest. */
function requireGateOn(output, context) {
    const bytes = regularBytes(output, 'initial-settings.json');
    if (hash(bytes) !== context.initialSettingsSha256) fail('device-query initial settings changed.');
    if (JSON.parse(bytes).AllowTitleReport !== true) fail('device-query evidence requires AllowTitleReport on in the retained initial settings.');
}

/** Full rederivation: paired journal, run log, layered adapter bytes and conclusions. */
export function checkDevqEvidence(output, evidence, runId, context) {
    if (context.devqAdapterId !== devqAdapterId || context.devqSuiteId !== devqSuiteId) fail('Missing or changed device-query adapter declaration.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) fail('device-query evidence requires every earlier grading layer.');
    const binding = evidence.devqEvidence;
    if (binding === undefined) fail('Missing device-query suite receipt binding.');
    const exact = binding && binding.runId === runId && binding.suiteId === devqSuiteId && binding.file === artifact.report && binding.runFile === artifact.run
        && binding.provisionalUntil === 'cell-receipt.json' && binding.contractsVersion === devqContractsVersion;
    if (!exact || !['sha256', 'runSha256', 'adapterBundleSha256'].every(key => isDigest(binding[key])) || context.devqContractsVersion !== binding.contractsVersion) {
        fail('Invalid device-query suite receipt binding.');
    }
    requireGateOn(output, context);
    const reportBytes = regularBytes(output, binding.file), runBytes = regularBytes(output, binding.runFile), journalBytes = regularBytes(output, artifact.journal);
    if (hash(reportBytes) !== binding.sha256) fail('device-query observation bytes changed.');
    if (hash(runBytes) !== binding.runSha256) fail('device-query run log bytes changed.');
    requireAdapter(output, binding, context);
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes), journalText = journalBytes.toString('utf8').trim();
    if (![report.adapter.bundleSha256 === binding.adapterBundleSha256, report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256,
        report.contractsVersion === binding.contractsVersion].every(Boolean)) fail('device-query report adapter or contract identity differs from the receipt.');
    if (!journalText) fail('device-query journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildDevqGradingReport(frames, run, runId, report.adapter, { contractsVersion: report.contractsVersion });
    if (!sameJson(report, derived)) fail('device-query observations do not match retained evidence.');
    if (!sameJson(frames, report.snapshots)) fail('device-query journal does not match the report.');
    if (!sameJson(devqCounts(report), binding.counts)) fail('device-query receipt counts do not match the report.');
}

/** The case ids this suite grades, exposed for the receipt's planned-case declarations. */
export { devqCaseIds } from './devq-contracts.mjs';
