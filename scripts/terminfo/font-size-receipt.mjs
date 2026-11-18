import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptFontSizeCollector, fontSizeAdapterId } from './font-size-grading-collector.mjs';
import { fontSizeSuiteId, fontSizeVersion } from './font-size-contracts.mjs';
import { buildFontSizeReport, fontSizeCounts } from './font-size-grading.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const artifacts = ['collector-font-size.mjs', 'font-size-run.json', 'font-size-snapshots.jsonl', 'font-size-observations.json'];
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = value => { if (!value) throw new Error('Invalid or incomplete OSC font-size receipt.'); };
const policyNumber = context => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(context.sourceSnapshotPolicy ?? '')?.[1] ?? 0);
export function includesFontSizeEvidence(output, evidence, context) {
    return policyNumber(context) >= 69 || evidence.fontSizeEvidence !== undefined
        || Object.keys(context).some(key => key.startsWith('fontSize'))
        || artifacts.some(file => lstatSync(join(output, file), { throwIfNoEntry: false }) !== undefined);
}
const bytes = (output, file) => {
    const path = join(output, file), stat = lstatSync(path);
    check(stat.isFile() && !stat.isSymbolicLink() && stat.size <= 16 * 1024 * 1024);
    return readFileSync(path);
};

export function checkFontSizeEvidence(output, evidence, runId, context) {
    check(lstatSync(join(output, 'font-size-failed-observations.jsonl'), { throwIfNoEntry: false }) === undefined);
    const binding = evidence.fontSizeEvidence;
    // Earlier captured workers/source graphs never performed this suite. Appending
    // coherent artifacts to an old cleanup proof must not manufacture qualification.
    check(policyNumber(context) >= 69 && context.fontSizeAdapterId === fontSizeAdapterId && context.fontSizeSuiteId === fontSizeSuiteId
        && context.fontSizeContractsVersion === fontSizeVersion && binding?.runId === runId
        && binding.suiteId === fontSizeSuiteId && binding.contractsVersion === fontSizeVersion
        && binding.file === artifacts[3] && binding.runFile === artifacts[1] && binding.provisionalUntil === 'cell-receipt.json');
    const adapterBytes = bytes(output, artifacts[0]), runBytes = bytes(output, artifacts[1]);
    const reportBytes = bytes(output, artifacts[3]);
    check(hash(adapterBytes) === binding.adapterBundleSha256 && binding.adapterBundleSha256 === context.fontSizeBundleSha256
        && hash(runBytes) === binding.runSha256 && hash(reportBytes) === binding.sha256);
    // The base owned adapter is independently verified by the required query receipt.
    const base = bytes(output, 'collector-adapter.mjs');
    check(hash(base) === context.adapterBundleSha256 && adapterBytes.equals(Buffer.from(adaptFontSizeCollector(base.toString('utf8')))));
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes);
    const frames = bytes(output, artifacts[2]).toString('utf8').trim().split('\n').map(line => JSON.parse(line));
    check(report.adapter.bundleSha256 === binding.adapterBundleSha256 && report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256);
    const cell = JSON.parse(bytes(output, 'cell-observations.json'));
    check(typeof frames[0]?.paneId === 'string' && cell.snapshots?.[0]?.paneId === frames[0].paneId);
    const derived = buildFontSizeReport(frames, run, runId, report.adapter);
    check(same(derived, report) && same(frames, report.snapshots) && same(fontSizeCounts(report), binding.counts));
}
