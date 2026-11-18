import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { buildReverseWideReport, reverseWideSuiteId } from './core-reverse-wide.mjs';

const files = ['rw-run.json', 'rw-observations.json', 'rw-snapshots.jsonl'];
const hash = value => createHash('sha256').update(value).digest('hex');
const check = (ok, message) => { if (!ok) throw new Error('Invalid Core reverse-video and wide-cell receipt: ' + message); };

export function includesReverseWideEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v44', 'readonly-owned-source-graph-v45', 'readonly-owned-source-graph-v46', 'readonly-owned-source-graph-v47', 'readonly-owned-source-graph-v48', 'readonly-owned-source-graph-v49', 'readonly-owned-source-graph-v50', 'readonly-owned-source-graph-v51', 'readonly-owned-source-graph-v52', 'readonly-owned-source-graph-v53', 'readonly-owned-source-graph-v54', 'readonly-owned-source-graph-v55', 'readonly-owned-source-graph-v56', 'readonly-owned-source-graph-v57', 'readonly-owned-source-graph-v58', 'readonly-owned-source-graph-v59', 'readonly-owned-source-graph-v60', 'readonly-owned-source-graph-v61', 'readonly-owned-source-graph-v62', 'readonly-owned-source-graph-v63', 'readonly-owned-source-graph-v64', 'readonly-owned-source-graph-v65', 'readonly-owned-source-graph-v66', 'readonly-owned-source-graph-v67', 'readonly-owned-source-graph-v68', 'readonly-owned-source-graph-v69'].includes(context.sourceSnapshotPolicy) || context.reverseWideSuiteId !== undefined || evidence.reverseWideEvidence !== undefined
        || files.some(file => lstatSync(join(output, file), { throwIfNoEntry: false }));
}

export function checkReverseWideEvidence(output, evidence, runId, context) {
    const binding = evidence.reverseWideEvidence;
    check(context.reverseWideSuiteId === reverseWideSuiteId && evidence.sgrRasterEvidence !== undefined && evidence.coreWrapEvidence !== undefined && evidence.tabStopsEvidence !== undefined
        && evidence.coreEraseEvidence !== undefined && evidence.textCursorEvidence !== undefined, 'suite or predecessor missing');
    check(binding?.runId === runId && binding.suiteId === reverseWideSuiteId
        && binding.file === 'rw-observations.json' && binding.runFile === 'rw-run.json'
        && binding.provisionalUntil === 'cell-receipt.json', 'binding mismatch');
    for (const file of files) {
        const stat = lstatSync(join(output, file));
        check(stat.isFile() && !stat.isSymbolicLink(), 'artifact is not a regular file');
    }
    const observation = readFileSync(join(output, binding.file)), run = readFileSync(join(output, binding.runFile));
    check(hash(observation) === binding.sha256 && hash(run) === binding.runSha256, 'artifact digest mismatch');
    const frames = readFileSync(join(output, 'rw-snapshots.jsonl'), 'utf8').trim().split('\n').map(JSON.parse);
    const derived = buildReverseWideReport(frames, JSON.parse(run), runId);
    check(JSON.stringify(derived) === JSON.stringify(JSON.parse(observation)), 'journal rederivation mismatch');
}

