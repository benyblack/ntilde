import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptReflowCollector, asciiReflowAdapterId } from './ascii-reflow-grading-collector.mjs';
import { buildReflowReport, reflowCounts } from './ascii-reflow-grading.mjs';
import { asciiReflowContractsVersion, asciiReflowSuiteId } from './ascii-reflow-contracts.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const check = value => { if (!value) throw new Error('Invalid or incomplete ASCII reflow receipt.'); };
export const asciiReflowSources = {
    asciiReflowContractsSourceSha256: 'ascii-reflow-contracts.mjs', asciiReflowObserverSourceSha256: 'ascii-reflow-observer.mjs',
    asciiReflowCollectorSourceSha256: 'ascii-reflow-collector.mjs', asciiReflowGradingSourceSha256: 'ascii-reflow-grading.mjs',
    asciiReflowGradingCollectorSourceSha256: 'ascii-reflow-grading-collector.mjs', asciiReflowReceiptSourceSha256: 'ascii-reflow-receipt.mjs',
};
export const asciiReflowPrefixFiles = ['rf-run.json', 'reflow-snapshots.jsonl', 'reflow-actions.jsonl', 'reflow-observations.json', 'reflow-unavailable.json', 'collector-rfl.mjs'];
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}
function bytes(output, file, maximum = 16 * 1024 * 1024) {
    const path = join(output, file), stat = lstatSync(path);
    if (!stat.isFile() || stat.isSymbolicLink() || stat.size > maximum) throw new Error(`Receipt artifact is not a regular bounded file: ${file}`);
    const value = readFileSync(path); check(value.length <= maximum); return value;
}
const journal = (output, file, maximum) => {
    const value = bytes(output, file, maximum).toString('utf8').trim();
    return value ? value.split('\n').map(line => JSON.parse(line)) : [];
};

/** Any retained ASCII reflow identity, source, action stream or parent declaration requires all checks. */
export function includesReflowEvidence(output, evidence, context) {
    return Number(/^readonly-owned-source-graph-v(\d+)$/.exec(context.sourceSnapshotPolicy ?? '')?.[1] ?? 0) >= 49
        || context.asciiReflowAdapterId !== undefined || context.asciiReflowSuiteId !== undefined
        || context.asciiReflowBundleSha256 !== undefined || context.asciiReflowContractsVersion !== undefined
        || context.ownedAsciiReflowPolicy !== undefined || evidence.reflowEvidence !== undefined
        || Object.keys(asciiReflowSources).some(key => context[key] !== undefined)
        || asciiReflowPrefixFiles.some(file => present(join(output, file)));
}

/** The private fixture and retained source copies must agree with the recorded capability and exact hashes. */
function checkFixtureAndSources(output, context, marker) {
    check(context.asciiReflowSuiteId === asciiReflowSuiteId && context.asciiReflowContractsVersion === asciiReflowContractsVersion
        && marker?.policy === context.ownedAsciiReflowPolicy && Array.isArray(marker.actions));
    const settingsBytes = bytes(output, 'initial-settings.json', 65536);
    check(hash(settingsBytes) === context.initialSettingsSha256);
    const settings = JSON.parse(settingsBytes);
    check(settings.AgentAccessObserveEnabled === true && settings.AgentAccessActEnabled === true
        && settings.SessionPersistence === 'Off' && settings.Profiles?.length === 1 && settings.Profiles[0].Type === 0);
    for (const [key, file] of Object.entries(asciiReflowSources))
        check(/^[a-f\d]{64}$/.test(context[key] ?? '') && hash(bytes(output, join('sources', file), 2 * 1024 * 1024)) === context[key]);
}

/** Re-derive both experiments, the four parent-selected actions and the pinned replay before finalizing. */
export function checkReflowEvidence(output, binding, runId, context, proof) {
    const marker = proof.proofs?.[0]?.asciiReflow;
    checkFixtureAndSources(output, context, marker);
    check(binding?.runId === runId && binding.suiteId === asciiReflowSuiteId
        && binding.contractsVersion === asciiReflowContractsVersion
        && ['reflow-observations.json', 'reflow-unavailable.json'].includes(binding.file));
    const reportBytes = bytes(output, binding.file);
    check(/^[a-f\d]{64}$/.test(binding.sha256 ?? '') && hash(reportBytes) === binding.sha256);
    const report = JSON.parse(reportBytes);
    check(report.runId === runId && report.suiteId === asciiReflowSuiteId);
    const frames = journal(output, 'reflow-snapshots.jsonl'), actions = journal(output, 'reflow-actions.jsonl', 256 * 1024);
    if (!same(marker.actions, actions)) throw new Error('Retained resize actions differ from the ownership proof.');
    const previous = bytes(output, 'collector-ost.mjs'), adapterBytes = bytes(output, 'collector-rfl.mjs');
    if (hash(previous) !== context.oscTitleBundleSha256 || hash(adapterBytes) !== context.asciiReflowBundleSha256
        || hash(adapterBytes) !== binding.adapterBundleSha256) throw new Error('Changed ASCII reflow evidence.');
    if (!adapterBytes.equals(Buffer.from(adaptReflowCollector(previous)))) throw new Error('Invalid ASCII reflow adapter derivation.');
    if (binding.file === 'reflow-unavailable.json') {
        check(report.status === 'unavailable' && ['screenDetailsUnavailable', 'asciiReflowGeometryUnavailable'].includes(report.reason)
            && report.fixtureStarted === false && report.plannedExperiments === 2 && frames.length === 0 && actions.length === 0
            && marker.paneId === undefined && marker.originalGeometry === undefined
            && binding.status === 'unavailable' && binding.reason === report.reason && same(binding.counts, {}));
        return;
    }
    const runBytes = bytes(output, 'rf-run.json');
    check(binding.file === 'reflow-observations.json' && binding.runFile === 'rf-run.json'
        && hash(runBytes) === binding.runSha256 && binding.provisionalUntil === 'cell-receipt.json');
    const run = JSON.parse(runBytes);
    const adapter = { id: asciiReflowAdapterId, derivedFromAdapterId: 'ntilde-owned-osc-title-grading-v1',
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
    check(marker.paneId === frames[0].paneId
        && same(marker.originalGeometry, { rows: frames[0].screen.rows, cols: frames[0].screen.cols }));
    if (!same(report, buildReflowReport(frames, actions, run, runId, adapter))) throw new Error('ASCII reflow report does not rederive.');
    validateOutputGeometry(proof.proofs[0].terminalOutput.description, frames[0].screen.rows, frames[0].screen.cols);
    check(same(binding.counts, reflowCounts(report)));
}
