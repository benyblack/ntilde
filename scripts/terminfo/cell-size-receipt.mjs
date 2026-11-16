import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptCellSizeCollector, cellSizeAdapterId } from './cell-size-grading-collector.mjs';
import { cellSizeSuiteId, cellSizeVersion } from './cell-size-contracts.mjs';
import { buildCellSizeReport, cellSizeCounts } from './cell-size-grading.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const artifacts = ['collector-cell-size.mjs', 'cell-size-run.json', 'cell-size-snapshots.jsonl', 'cell-size-observations.json'];
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = value => { if (!value) throw new Error('Invalid or incomplete OSC cell-size receipt.'); };
const policyNumber = context => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(context.sourceSnapshotPolicy ?? '')?.[1] ?? 0);
export function includesCellSizeEvidence(output, evidence, context) {
    return policyNumber(context) >= 63 || evidence.cellSizeEvidence !== undefined
        || ['cellSizeAdapterId', 'cellSizeSuiteId', 'cellSizeBundleSha256', 'cellSizeContractsVersion'].some(key => context[key] !== undefined)
        || artifacts.some(file => lstatSync(join(output, file), { throwIfNoEntry: false }) !== undefined);
}
const bytes = (output, file) => {
    const path = join(output, file), stat = lstatSync(path);
    check(stat.isFile() && !stat.isSymbolicLink() && stat.size <= 16 * 1024 * 1024);
    return readFileSync(path);
};

export function checkCellSizeEvidence(output, evidence, runId, context) {
    const binding = evidence.cellSizeEvidence;
    check(context.cellSizeAdapterId === cellSizeAdapterId && context.cellSizeSuiteId === cellSizeSuiteId
        && context.cellSizeContractsVersion === cellSizeVersion && binding?.runId === runId
        && binding.suiteId === cellSizeSuiteId && binding.contractsVersion === cellSizeVersion
        && binding.file === artifacts[3] && binding.runFile === artifacts[1] && binding.provisionalUntil === 'cell-receipt.json');
    const adapterBytes = bytes(output, artifacts[0]), runBytes = bytes(output, artifacts[1]);
    const reportBytes = bytes(output, artifacts[3]);
    check(hash(adapterBytes) === binding.adapterBundleSha256 && binding.adapterBundleSha256 === context.cellSizeBundleSha256
        && hash(runBytes) === binding.runSha256 && hash(reportBytes) === binding.sha256);
    // The base owned adapter is independently verified by the required query receipt.
    const base = bytes(output, 'collector-adapter.mjs');
    check(hash(base) === context.adapterBundleSha256 && adapterBytes.equals(Buffer.from(adaptCellSizeCollector(base.toString('utf8')))));
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes);
    const frames = bytes(output, artifacts[2]).toString('utf8').trim().split('\n').map(line => JSON.parse(line));
    check(report.adapter.bundleSha256 === binding.adapterBundleSha256 && report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256);
    const derived = buildCellSizeReport(frames, run, runId, report.adapter);
    check(same(derived, report) && same(frames, report.snapshots) && same(cellSizeCounts(report), binding.counts));
}
