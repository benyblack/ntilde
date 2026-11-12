import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptOscTitleCollector, oscTitleAdapterId } from './osc-title-grading-collector.mjs';
import { buildOscTitleReport, oscTitleCounts } from './osc-title-grading.mjs';
import { oscTitleContractsVersion, oscTitleSuiteId } from './osc-title-contracts.mjs';

const hash = value => createHash('sha256').update(value).digest('hex');
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const read = (output, file) => {
    const path = join(output, file);
    if (!lstatSync(path).isFile()) throw new Error(`OSC title artifact is not a regular file: ${file}`);
    return readFileSync(path);
};
export function includesOscTitleEvidence(output, evidence, context) {
    return Number(/^readonly-owned-source-graph-v(\d+)$/.exec(context.sourceSnapshotPolicy ?? '')?.[1] ?? 0) >= 47
        || context.oscTitleAdapterId !== undefined || evidence.oscTitleEvidence !== undefined
        || ['ost-run.json', 'ost-snapshots.jsonl', 'ost-observations.json', 'collector-ost.mjs']
            .some(file => lstatSync(join(output, file), { throwIfNoEntry: false }) !== undefined);
}
export function checkOscTitleEvidence(output, evidence, runId, context) {
    const binding = evidence.oscTitleEvidence;
    if (!evidence.titleStackEvidence || context.oscTitleAdapterId !== oscTitleAdapterId || context.oscTitleSuiteId !== oscTitleSuiteId
        || context.oscTitleContractsVersion !== oscTitleContractsVersion || binding?.runId !== runId || binding?.suiteId !== oscTitleSuiteId
        || binding?.contractsVersion !== oscTitleContractsVersion || binding?.provisionalUntil !== 'cell-receipt.json'
        || binding.file !== 'ost-observations.json' || binding.runFile !== 'ost-run.json') throw new Error('Invalid OSC title receipt identity.');
    const adapter = read(output, 'collector-ost.mjs'), previous = read(output, 'collector-tst.mjs');
    if (hash(previous) !== context.titleStackBundleSha256 || hash(adapter) !== context.oscTitleBundleSha256
        || hash(adapter) !== binding.adapterBundleSha256 || !adapter.equals(Buffer.from(adaptOscTitleCollector(previous)))) throw new Error('Invalid OSC title adapter derivation.');
    const reportBytes = read(output, 'ost-observations.json'), runBytes = read(output, 'ost-run.json'), journal = read(output, 'ost-snapshots.jsonl');
    if (hash(reportBytes) !== binding.sha256 || hash(runBytes) !== binding.runSha256) throw new Error('Changed OSC title evidence.');
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes), frames = journal.toString('utf8').trim().split('\n').map(line => JSON.parse(line));
    if (report.adapter?.bundleSha256 !== hash(adapter) || report.adapter?.upstreamBundleSha256 !== context.upstreamBundleSha256
        || !same(report, buildOscTitleReport(frames, run, runId, report.adapter)) || !same(binding.counts, oscTitleCounts(report))) {
        throw new Error('OSC title report does not rederive.');
    }
}
