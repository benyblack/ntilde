// Receipt binding for the selection-color qualification (extensions.osc17-highlight-bg, osc19-highlight-fg, osc117-reset-highlight-bg,
// osc119-reset-highlight-fg). The graded evidence stays provisional until the shared cleanup receipt exists, and the check re-derives the
// whole report from retained bytes (journal, run log, the original PNGs and the layered adapter) without the collector. A run carrying
// this evidence must also carry the runtime-color reset suite (the layer whose extension this one derives beside): these conclusions sit
// beside theirs, never replace them. Selection colors are runtime session state, so the cleanup receipt's verified restoration of the
// pristine theme and render is what makes the suite's own restoration claims checkable.
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptSelectionColorsCollector, selectionColorsAdapterId } from './selection-colors-grading-collector.mjs';
import { buildSelectionColorsGradingReport, selectionColorsCounts } from './selection-colors-grading.mjs';
import { selectionColorsContractsVersion, selectionColorsSuiteId } from './selection-colors-contracts.mjs';

const hash = value => createHash('sha256').update(value).digest('hex');
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const read = (output, file) => {
    const path = join(output, file);
    if (!lstatSync(path).isFile()) throw new Error(`Selection-color artifact is not a regular file: ${file}`);
    return readFileSync(path);
};
export function includesSelectionColorsEvidence(output, evidence, context) {
    return Number(/^readonly-owned-source-graph-v(\d+)$/.exec(context.sourceSnapshotPolicy ?? '')?.[1] ?? 0) >= 52
        || context.selectionColorsAdapterId !== undefined || context.selectionColorsSuiteId !== undefined || context.selectionColorsBundleSha256 !== undefined
        || context.selectionColorsContractsVersion !== undefined || evidence.selectionColorsEvidence !== undefined
        || ['selc-run.json', 'selc-snapshots.jsonl', 'selc-observations.json', 'collector-selc.mjs']
            .some(file => lstatSync(join(output, file), { throwIfNoEntry: false }) !== undefined);
}
export function checkSelectionColorsEvidence(output, evidence, runId, context) {
    const binding = evidence.selectionColorsEvidence;
    if (!evidence.colorResetEvidence || context.selectionColorsAdapterId !== selectionColorsAdapterId || context.selectionColorsSuiteId !== selectionColorsSuiteId
        || context.selectionColorsContractsVersion !== selectionColorsContractsVersion || binding?.runId !== runId || binding?.suiteId !== selectionColorsSuiteId
        || binding?.contractsVersion !== selectionColorsContractsVersion || binding?.provisionalUntil !== 'cell-receipt.json') throw new Error('Invalid selection-color receipt identity.');
    const adapter = read(output, 'collector-selc.mjs'), previous = read(output, 'collector-ost.mjs');
    if (hash(previous) !== context.oscTitleBundleSha256 || hash(adapter) !== context.selectionColorsBundleSha256
        || hash(adapter) !== binding.adapterBundleSha256 || !adapter.equals(Buffer.from(adaptSelectionColorsCollector(previous)))) throw new Error('Invalid selection-color adapter derivation.');
    if (binding.file !== 'selc-observations.json' || binding.runFile !== 'selc-run.json') throw new Error('Invalid selection-color receipt paths.');
    const reportBytes = read(output, binding.file), runBytes = read(output, binding.runFile), journal = read(output, 'selc-snapshots.jsonl');
    if (hash(reportBytes) !== binding.sha256 || hash(runBytes) !== binding.runSha256) throw new Error('Changed selection-color evidence.');
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes), frames = journal.toString('utf8').trim().split('\n').map(line => JSON.parse(line));
    if (report.adapter?.bundleSha256 !== hash(adapter) || report.adapter?.upstreamBundleSha256 !== context.upstreamBundleSha256
        || !same(report, buildSelectionColorsGradingReport(frames, run, runId, report.adapter, { output }))
        || !same(binding.counts, selectionColorsCounts(report))) {
        throw new Error('Selection-color report does not rederive.');
    }
}
