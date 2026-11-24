import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { buildModernModesReport, modernModesSuiteId } from './modern-modes.mjs';

const runFile = 'mmod-run.json';
const journalFile = 'mmod-snapshots.jsonl';
const reportFile = 'mmod-observations.json';
const failedArtifacts = ['mmod-failed-observations.jsonl', 'mmod-failed-exchanges.json'];
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const exists = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;
const check = (ok, reason) => { if (!ok) throw new Error('Invalid Modern modes receipt: ' + reason); };

export function includesModernModesEvidence(output, evidence, context) {
    return Number(/^readonly-owned-source-graph-v(\d+)$/.exec(context.sourceSnapshotPolicy ?? '')?.[1]) >= 74
        || context.modernModesSuiteId !== undefined || evidence.modernModesEvidence !== undefined
        || [runFile, journalFile, reportFile, ...failedArtifacts].some(file => exists(join(output, file)));
}

function readArtifact(output, file) {
    const path = join(output, file);
    const stat = lstatSync(path, { throwIfNoEntry: false });
    check(stat?.isFile() && stat.size <= 1024 * 1024, 'missing, non-regular or oversized ' + file);
    return readFileSync(path);
}

export function deriveModernModesEvidence(output, runId) {
    const run = readArtifact(output, runFile), journal = readArtifact(output, journalFile);
    const frames = journal.toString('utf8').trimEnd().split('\n').map(line => JSON.parse(line));
    const report = buildModernModesReport(frames, JSON.parse(run), runId);
    return { report, paneId: frames[0].paneId, binding: { runId, suiteId: modernModesSuiteId, file: reportFile,
        sha256: hash(JSON.stringify(report, null, 2) + '\n'), runFile, runSha256: hash(run),
        journalFile, journalSha256: hash(journal), provisionalUntil: 'cell-receipt.json' } };
}

export function checkModernModesEvidence(output, evidence, runId, context) {
    const binding = evidence.modernModesEvidence;
    check(context.modernModesSuiteId === modernModesSuiteId && evidence.modernCursorEvidence
        && binding?.runId === runId && binding.suiteId === modernModesSuiteId, 'suite, predecessor or binding missing');
    check(!failedArtifacts.some(file => exists(join(output, file))), 'failed observation present');
    const derived = deriveModernModesEvidence(output, runId);
    const cell = JSON.parse(readFileSync(join(output, 'cell-observations.json'))).snapshots[0];
    check(derived.paneId === cell.paneId && derived.report.geometry.rows === cell.screen.rows
        && derived.report.geometry.cols === cell.screen.cols, 'mode journal is from another pane or grid');
    check(hash(readArtifact(output, reportFile)) === derived.binding.sha256
        && JSON.stringify(binding) === JSON.stringify(derived.binding), 'artifact digest or rederivation mismatch');
}
