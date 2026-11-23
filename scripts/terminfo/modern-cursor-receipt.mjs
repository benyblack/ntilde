import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { buildModernCursorReport, modernCursorSuiteId } from './modern-cursor.mjs';

const runFile = 'mcur-run.json';
const journalFile = 'mcur-snapshots.jsonl';
const reportFile = 'mcur-observations.json';
const failureFile = 'mcur-failed-observations.jsonl';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const exists = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;
const check = (ok, reason) => { if (!ok) throw new Error('Invalid Modern cursor receipt: ' + reason); };

export function includesModernCursorEvidence(output, evidence, context) {
    return Number(/^readonly-owned-source-graph-v(\d+)$/.exec(context.sourceSnapshotPolicy ?? '')?.[1]) >= 73
        || context.modernCursorSuiteId !== undefined || evidence.modernCursorEvidence !== undefined
        || [runFile, journalFile, reportFile, failureFile].some(file => exists(join(output, file)));
}

function readArtifact(output, file) {
    const path = join(output, file);
    const stat = lstatSync(path, { throwIfNoEntry: false });
    check(stat?.isFile() && stat.size <= 1024 * 1024, 'missing, non-regular or oversized ' + file);
    return readFileSync(path);
}

export function deriveModernCursorEvidence(output, runId) {
    const run = readArtifact(output, runFile), journal = readArtifact(output, journalFile);
    const frames = journal.toString('utf8').trimEnd().split('\n').map(line => JSON.parse(line));
    const report = buildModernCursorReport(frames, JSON.parse(run), runId);
    return { report, binding: { runId, suiteId: modernCursorSuiteId, file: reportFile,
        sha256: hash(JSON.stringify(report, null, 2) + '\n'), runFile, runSha256: hash(run),
        journalFile, journalSha256: hash(journal), provisionalUntil: 'cell-receipt.json' } };
}

export function checkModernCursorEvidence(output, evidence, runId, context) {
    const binding = evidence.modernCursorEvidence;
    check(context.modernCursorSuiteId === modernCursorSuiteId && evidence.textCursorEvidence
        && binding?.runId === runId && binding.suiteId === modernCursorSuiteId, 'suite, predecessor or binding missing');
    check(!exists(join(output, failureFile)), 'failed observation present');
    const derived = deriveModernCursorEvidence(output, runId);
    check(hash(readArtifact(output, reportFile)) === derived.binding.sha256
        && JSON.stringify(binding) === JSON.stringify(derived.binding), 'artifact digest or rederivation mismatch');
}
