import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { buildEraseReport, eraseSuiteId } from './core-erase.mjs';

const files = ['ce-run.json', 'ce-observations.json', 'ce-snapshots.jsonl'];
const hash = value => createHash('sha256').update(value).digest('hex');
const check = (ok, message) => { if (!ok) throw new Error('Invalid Core erase receipt: ' + message); };

export function includesCoreEraseEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v40', 'readonly-owned-source-graph-v41', 'readonly-owned-source-graph-v42', 'readonly-owned-source-graph-v43', 'readonly-owned-source-graph-v44', 'readonly-owned-source-graph-v45', 'readonly-owned-source-graph-v46', 'readonly-owned-source-graph-v48'].includes(context.sourceSnapshotPolicy) || context.coreEraseSuiteId !== undefined || evidence.coreEraseEvidence !== undefined
        || files.some(file => lstatSync(join(output, file), { throwIfNoEntry: false }));
}

export function checkCoreEraseEvidence(output, evidence, runId, context) {
    const binding = evidence.coreEraseEvidence;
    check(context.coreEraseSuiteId === eraseSuiteId && evidence.textCursorEvidence !== undefined && evidence.osc133Evidence !== undefined, 'suite or predecessor missing');
    check(binding?.runId === runId && binding.suiteId === eraseSuiteId
        && binding.file === 'ce-observations.json' && binding.runFile === 'ce-run.json'
        && binding.provisionalUntil === 'cell-receipt.json', 'binding mismatch');
    for (const file of files) {
        const stat = lstatSync(join(output, file));
        check(stat.isFile() && !stat.isSymbolicLink(), 'artifact is not a regular file');
    }
    const observation = readFileSync(join(output, binding.file)), run = readFileSync(join(output, binding.runFile));
    check(hash(observation) === binding.sha256 && hash(run) === binding.runSha256, 'artifact digest mismatch');
    const frames = readFileSync(join(output, 'ce-snapshots.jsonl'), 'utf8').trim().split('\n').map(JSON.parse);
    const derived = buildEraseReport(frames, JSON.parse(run), runId);
    check(JSON.stringify(derived) === JSON.stringify(JSON.parse(observation)), 'journal rederivation mismatch');
}
