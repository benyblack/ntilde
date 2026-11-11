import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { deriveWrapEvidence } from './core-wrap-contract.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';
const files = ['cw-run.json', 'cw-observations.json', 'cw-snapshots.jsonl', 'cw-geometry.json'];
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (ok, message) => { if (!ok) throw new Error('Invalid Core wrap receipt: ' + message); };
export function includesCoreWrapEvidence(output, evidence, context, proof) {
    return ['readonly-owned-source-graph-v42', 'readonly-owned-source-graph-v43', 'readonly-owned-source-graph-v44', 'readonly-owned-source-graph-v45'].includes(context.sourceSnapshotPolicy) || context.coreWrapSuiteId !== undefined
        || evidence.coreWrapEvidence !== undefined || proof.proofs?.some(p => p.coreWrap !== undefined)
        || files.some(file => lstatSync(join(output, file), { throwIfNoEntry: false }));
}
export function checkCoreWrapEvidence(output, evidence, runId, context, proof) {
    const binding = evidence.coreWrapEvidence;
    check(context.coreWrapSuiteId === 'ntilde-core-wrap-v1' && evidence.coreEraseEvidence !== undefined && evidence.tabStopsEvidence !== undefined, 'missing predecessor');
    check(binding?.runId === runId && binding.suiteId === context.coreWrapSuiteId
        && binding.file === 'cw-observations.json' && binding.runFile === 'cw-run.json'
        && binding.geometryFile === 'cw-geometry.json' && binding.provisionalUntil === 'cell-receipt.json', 'binding mismatch');
    for (const file of files) { const stat = lstatSync(join(output, file)); check(stat.isFile() && !stat.isSymbolicLink(), 'invalid file'); }
    const read = file => readFileSync(join(output, file));
    const observation = read(binding.file), run = read(binding.runFile), geometryBytes = read(binding.geometryFile);
    check(hash(observation) === binding.sha256 && hash(run) === binding.runSha256
        && hash(geometryBytes) === binding.geometrySha256, 'digest mismatch');
    const geometry = JSON.parse(geometryBytes), frames = read('cw-snapshots.jsonl').toString().trim().split('\n').map(JSON.parse);
    check(same(proof.proofs?.[0]?.coreWrap, geometry), 'geometry proof mismatch');
    const baseline = JSON.parse(read('cell-observations.json')).snapshots[0];
    check(baseline.paneId === geometry.paneId && baseline.screen.rows === geometry.original.rows
        && baseline.screen.cols === geometry.original.cols, 'predecessor pane or dimensions changed');
    validateOutputGeometry(proof.proofs[0].terminalOutput.description, geometry.original.rows, geometry.original.cols);
    const derived = deriveWrapEvidence(frames, JSON.parse(run), runId, geometry);
    check(same(derived, JSON.parse(observation)), 'journal rederivation mismatch');
}
