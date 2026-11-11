import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { buildTabStopsReport, tabStopsSuiteId } from './core-tab-stops.mjs';

const files = ['ts-run.json', 'ts-observations.json', 'ts-snapshots.jsonl'];
const hash = value => createHash('sha256').update(value).digest('hex');
const check = (ok, message) => { if (!ok) throw new Error('Invalid Core tab-stops receipt: ' + message); };

export function includesTabStopsEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v41', 'readonly-owned-source-graph-v42'].includes(context.sourceSnapshotPolicy) || context.tabStopsSuiteId !== undefined || evidence.tabStopsEvidence !== undefined
        || files.some(file => lstatSync(join(output, file), { throwIfNoEntry: false }));
}

export function checkTabStopsEvidence(output, evidence, runId, context) {
    const binding = evidence.tabStopsEvidence;
    check(context.tabStopsSuiteId === tabStopsSuiteId && evidence.coreEraseEvidence !== undefined && evidence.textCursorEvidence !== undefined, 'suite or predecessor missing');
    check(binding?.runId === runId && binding.suiteId === tabStopsSuiteId
        && binding.file === 'ts-observations.json' && binding.runFile === 'ts-run.json'
        && binding.provisionalUntil === 'cell-receipt.json', 'binding mismatch');
    for (const file of files) {
        const stat = lstatSync(join(output, file));
        check(stat.isFile() && !stat.isSymbolicLink(), 'artifact is not a regular file');
    }
    const observation = readFileSync(join(output, binding.file)), run = readFileSync(join(output, binding.runFile));
    check(hash(observation) === binding.sha256 && hash(run) === binding.runSha256, 'artifact digest mismatch');
    const frames = readFileSync(join(output, 'ts-snapshots.jsonl'), 'utf8').trim().split('\n').map(JSON.parse);
    const derived = buildTabStopsReport(frames, JSON.parse(run), runId);
    check(JSON.stringify(derived) === JSON.stringify(JSON.parse(observation)), 'journal rederivation mismatch');
}
