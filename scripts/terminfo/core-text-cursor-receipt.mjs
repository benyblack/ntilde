import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { buildTextCursorReport, textCursorSuiteId } from './core-text-cursor.mjs';

const files = ['tc-run.json', 'tc-observations.json', 'tc-snapshots.jsonl'];
const hash = value => createHash('sha256').update(value).digest('hex');
const check = (ok, message) => { if (!ok) throw new Error('Invalid Core text/cursor receipt: ' + message); };

export function includesTextCursorEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v38', 'readonly-owned-source-graph-v39', 'readonly-owned-source-graph-v40', 'readonly-owned-source-graph-v41', 'readonly-owned-source-graph-v42', 'readonly-owned-source-graph-v43', 'readonly-owned-source-graph-v44', 'readonly-owned-source-graph-v45', 'readonly-owned-source-graph-v46', 'readonly-owned-source-graph-v48'].includes(context.sourceSnapshotPolicy) || context.textCursorSuiteId !== undefined || evidence.textCursorEvidence !== undefined
        || files.some(file => lstatSync(join(output, file), { throwIfNoEntry: false }));
}

export function checkTextCursorEvidence(output, evidence, runId, context) {
    const binding = evidence.textCursorEvidence;
    check(context.textCursorSuiteId === textCursorSuiteId && evidence.coreGapsEvidence !== undefined
        && evidence.colorResetEvidence !== undefined, 'suite or predecessor missing');
    check(binding?.runId === runId && binding.suiteId === textCursorSuiteId
        && binding.file === 'tc-observations.json' && binding.runFile === 'tc-run.json'
        && binding.provisionalUntil === 'cell-receipt.json', 'binding mismatch');
    for (const file of files) {
        const stat = lstatSync(join(output, file));
        check(stat.isFile() && !stat.isSymbolicLink(), 'artifact is not a regular file');
    }
    const observation = readFileSync(join(output, binding.file)), run = readFileSync(join(output, binding.runFile));
    check(hash(observation) === binding.sha256 && hash(run) === binding.runSha256, 'artifact digest mismatch');
    const frames = readFileSync(join(output, 'tc-snapshots.jsonl'), 'utf8').trim().split('\n').map(JSON.parse);
    const derived = buildTextCursorReport(frames, JSON.parse(run), runId);
    check(JSON.stringify(derived) === JSON.stringify(JSON.parse(observation)), 'journal rederivation mismatch');
}
