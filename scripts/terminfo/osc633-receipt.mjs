// The OSC 633 suffix is provisional until the shared owned cleanup receipt.
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptOsc633Collector, osc633AdapterId, osc633CaseIds } from './osc633-grading-collector.mjs';
import { osc633ContractsVersion, osc633SuiteId } from './osc633-contracts.mjs';
import { buildOsc633GradingReport, osc633Counts } from './osc633-grading.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const digest = value => /^[a-f\d]{64}$/.test(value ?? '');
const files = ['vs633-observations.json', 'vs633-run.json', 'vs633-snapshots.jsonl', 'vs633-unavailable.json', 'collector-vs633.mjs'];
const unavailableReasons = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);
const check = (value, message) => { if (!value) throw new Error(message); };
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;

export function includesOsc633Evidence(output, evidence, context) {
    const policy = Number(/^readonly-owned-source-graph-v(\d+)$/.exec(context.sourceSnapshotPolicy ?? '')?.[1] ?? 0);
    return policy >= 62 || context.osc633AdapterId !== undefined || context.osc633SuiteId !== undefined || context.osc633BundleSha256 !== undefined
        || context.osc633ContractsVersion !== undefined || evidence.osc633Evidence !== undefined || files.some(file => present(join(output, file)));
}

function regular(output, name) {
    const path = join(output, name);
    check(lstatSync(path).isFile(), `OSC 633 artifact is not a regular file: ${name}`);
    return readFileSync(path);
}

export function checkOsc633Evidence(output, evidence, runId, context) {
    check(context.osc633AdapterId === osc633AdapterId && context.osc633SuiteId === osc633SuiteId
        && context.osc633ContractsVersion === osc633ContractsVersion && evidence.osc133Evidence !== undefined, 'Missing OSC 633 or prerequisite identity.');
    const binding = evidence.osc633Evidence;
    check(binding?.runId === runId && binding.suiteId === osc633SuiteId && binding.contractsVersion === osc633ContractsVersion
        && binding.provisionalUntil === 'cell-receipt.json' && digest(binding.sha256) && digest(binding.adapterBundleSha256), 'Invalid OSC 633 binding.');
    const adapter = regular(output, 'collector-vs633.mjs'), predecessor = regular(output, 'collector-shi.mjs');
    check(hash(adapter) === binding.adapterBundleSha256 && context.osc633BundleSha256 === binding.adapterBundleSha256
        && hash(predecessor) === context.osc133BundleSha256 && Buffer.compare(Buffer.from(adaptOsc633Collector(predecessor.toString())), adapter) === 0,
    'OSC 633 adapter does not match the retained OSC 133 derivation.');
    if (binding.status === 'unavailable') {
        check(binding.file === 'vs633-unavailable.json' && unavailableReasons.has(binding.reason) && same(binding.counts, {}), 'Invalid OSC 633 unavailability.');
        check(!['vs633-observations.json', 'vs633-run.json', 'vs633-snapshots.jsonl'].some(name => present(join(output, name)) && readFileSync(join(output, name)).length), 'Unavailable OSC 633 has partial evidence.');
        const bytes = regular(output, binding.file), marker = JSON.parse(bytes);
        check(hash(bytes) === binding.sha256 && marker.schemaVersion === 1 && marker.runId === runId && marker.suiteId === osc633SuiteId
            && marker.status === 'unavailable' && marker.reason === binding.reason && marker.plannedCases === osc633CaseIds.length, 'OSC 633 unavailable marker changed.');
        return;
    }
    check(binding.file === 'vs633-observations.json' && binding.runFile === 'vs633-run.json' && digest(binding.runSha256), 'Invalid OSC 633 files.');
    const bytes = regular(output, binding.file), runBytes = regular(output, binding.runFile), journal = regular(output, 'vs633-snapshots.jsonl').toString().trim();
    check(hash(bytes) === binding.sha256 && hash(runBytes) === binding.runSha256 && journal.length > 0, 'OSC 633 retained bytes changed.');
    const report = JSON.parse(bytes), run = JSON.parse(runBytes), frames = journal.split('\n').map(line => JSON.parse(line));
    check(report.adapter.bundleSha256 === binding.adapterBundleSha256 && report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256,
        'OSC 633 report adapter identity changed.');
    const derived = buildOsc633GradingReport(frames, run, runId, report.adapter);
    check(same(report, derived) && same(osc633Counts(report), binding.counts), 'OSC 633 report does not rederive.');
}
