import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { buildSgrRasterReport, sgrRasterSuiteId, sgrRasterDirectory } from './core-sgr-raster.mjs';

const files = ['sr-run.json', 'sr-observations.json', 'sr-snapshots.jsonl'];
const hash = value => createHash('sha256').update(value).digest('hex');
const check = (ok, message) => { if (!ok) throw new Error('Invalid Core SGR raster receipt: ' + message); };

/** The policy-v42 worker collects this suite unconditionally; any declaration or retained artifact also requires it. */
export function includesSgrRasterEvidence(output, evidence, context) {
    return context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v43' || context.sgrRasterSuiteId !== undefined || evidence.sgrRasterEvidence !== undefined
        || files.some(file => lstatSync(join(output, file), { throwIfNoEntry: false }))
        || lstatSync(join(output, sgrRasterDirectory), { throwIfNoEntry: false }) !== undefined;
}

/** Full rederivation from the retained journal, run log and original PNGs; the render directory holds exactly the PNGs the journal names. */
export function checkSgrRasterEvidence(output, evidence, runId, context) {
    const binding = evidence.sgrRasterEvidence;
    check(context.sgrRasterSuiteId === sgrRasterSuiteId && evidence.tabStopsEvidence !== undefined && evidence.coreWrapEvidence !== undefined && evidence.coreEraseEvidence !== undefined, 'suite or predecessor missing');
    check(binding?.runId === runId && binding.suiteId === sgrRasterSuiteId && binding.file === 'sr-observations.json' && binding.runFile === 'sr-run.json'
        && binding.provisionalUntil === 'cell-receipt.json', 'binding mismatch');
    for (const file of files) {
        const stat = lstatSync(join(output, file));
        check(stat.isFile() && !stat.isSymbolicLink(), 'artifact is not a regular file');
    }
    check(lstatSync(join(output, sgrRasterDirectory)).isDirectory(), 'renders are not a directory');
    for (const entry of readdirSync(join(output, sgrRasterDirectory), { withFileTypes: true })) check(entry.isFile() && /^frame-\d{3}\.png$/.test(entry.name), 'render is not a regular frame file');
    const observation = readFileSync(join(output, binding.file)), run = readFileSync(join(output, binding.runFile));
    check(hash(observation) === binding.sha256 && hash(run) === binding.runSha256, 'artifact digest mismatch');
    const frames = readFileSync(join(output, 'sr-snapshots.jsonl'), 'utf8').trim().split('\n').map(JSON.parse);
    const named = frames.map(frame => frame.pixel?.file?.split('/')[1]).sort();
    check(JSON.stringify(readdirSync(join(output, sgrRasterDirectory)).sort()) === JSON.stringify(named), 'renders are not exactly the journal captures');
    const derived = buildSgrRasterReport(frames, JSON.parse(run), runId, output);
    check(JSON.stringify(derived) === JSON.stringify(JSON.parse(observation)), 'journal rederivation mismatch');
}
