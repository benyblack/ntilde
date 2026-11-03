import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { buildSavedReport } from './saved.mjs';
import { savedSuiteId, ownedSavedPolicy } from './saved-cases.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const check = value => { if (!value) throw new Error('Invalid or incomplete v7 owned saved-state receipt.'); };
export const savedSources = {
    savedCasesSourceSha256: 'saved-cases.mjs', savedCaptureSourceSha256: 'saved-capture.mjs',
    savedSuiteSourceSha256: 'saved.mjs', savedExpectationsSourceSha256: 'saved-expectations.mjs', savedReceiptSourceSha256: 'saved-receipt.mjs',
};
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}
function bytes(output, file, maximum = 16 * 1024 * 1024) {
    const path = join(output, file), stat = lstatSync(path);
    check(stat.isFile() && !stat.isSymbolicLink() && stat.size <= maximum);
    const value = readFileSync(path); check(value.length <= maximum); return value;
}
/** Any retained suite/source/journal or parent declaration makes the complete v7 chain mandatory. */
export function includesSavedEvidence(output, evidence, context, proof) {
    return context.savedSuiteId !== undefined || context.ownedSavedPolicy !== undefined || evidence.savedEvidence !== undefined
        || Object.keys(savedSources).some(key => context[key] !== undefined)
        || proof.proofs?.some(value => value.saved !== undefined) === true
        || present(join(output, 'saved-snapshots.jsonl'))
        || Object.values(savedSources).some(file => present(join(output, 'sources', file)));
}
function checkSources(output, context, marker) {
    check(context.savedSuiteId === savedSuiteId && context.ownedSavedPolicy === ownedSavedPolicy && marker?.policy === ownedSavedPolicy
        && Object.keys(marker).every(key => ['policy', 'paneId', 'geometry'].includes(key)));
    const dir = lstatSync(join(output, 'sources')); check(dir.isDirectory() && !dir.isSymbolicLink());
    for (const [key, file] of Object.entries(savedSources)) {
        check(/^[a-f\d]{64}$/.test(context[key] ?? '') && hash(bytes(output, join('sources', file), 2 * 1024 * 1024)) === context[key]);
    }
}
/** Re-derive every staged active-state observation and match its original common-pane/kernel proof. */
export function checkSavedEvidence(output, binding, runId, context, proof) {
    const marker = proof.proofs?.[0]?.saved;
    checkSources(output, context, marker);
    check(binding?.runId === runId && binding.suiteId === savedSuiteId
        && ['saved-observations.json', 'saved-unavailable.json'].includes(binding.file));
    check(Object.keys(binding).every(key => ['runId', 'suiteId', 'file', 'sha256', 'counts', 'provisionalUntil', 'status', 'reason'].includes(key)));
    const reportBytes = bytes(output, binding.file);
    check(/^[a-f\d]{64}$/.test(binding.sha256 ?? '') && hash(reportBytes) === binding.sha256);
    const report = JSON.parse(reportBytes);
    check(report.runId === runId && report.suiteId === savedSuiteId);
    const text = bytes(output, 'saved-snapshots.jsonl').toString('utf8').trim();
    const frames = text ? text.split('\n').map(line => JSON.parse(line)) : [];
    if (binding.file === 'saved-unavailable.json') {
        check(report.status === 'unavailable' && ['screenDetailsUnavailable', 'savedGeometryUnavailable'].includes(report.reason)
            && report.fixtureStarted === false && report.plannedExperiments === 9 && frames.length === 0
            && marker.paneId === undefined && marker.geometry === undefined
            && binding.status === 'unavailable' && binding.reason === report.reason && same(binding.counts, {}));
        return;
    }
    check(binding.status === undefined && binding.reason === undefined);
    const derived = buildSavedReport(frames, runId);
    check(same(report, derived) && marker.paneId === frames[0].paneId
        && same(marker.geometry, { rows: frames[0].screen.rows, cols: frames[0].screen.cols }));
    const cell = JSON.parse(bytes(output, 'cell-observations.json'));
    check(cell.snapshots?.[0]?.paneId === marker.paneId && cell.snapshots[0].screen.rows === marker.geometry.rows
        && cell.snapshots[0].screen.cols === marker.geometry.cols);
    validateOutputGeometry(proof.proofs[0].terminalOutput.description, marker.geometry.rows, marker.geometry.cols);
    const counts = report.observations.reduce((values, item) => ({ ...values, [item.result]: (values[item.result] ?? 0) + 1 }), {});
    check(same(counts, binding.counts));
}
