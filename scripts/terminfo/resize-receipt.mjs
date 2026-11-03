import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { buildResizeReport } from './resize.mjs';
import { resizeSuiteId, ownedResizePolicy } from './resize-cases.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const check = value => { if (!value) throw new Error('Invalid or incomplete v6 owned resize receipt.'); };
export const resizeSources = {
    resizeCasesSourceSha256: 'resize-cases.mjs', resizeCaptureSourceSha256: 'resize-capture.mjs',
    resizeSuiteSourceSha256: 'resize.mjs', resizeActionSourceSha256: 'resize-action.mjs', resizeReceiptSourceSha256: 'resize-receipt.mjs',
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
const json = (output, file, maximum) => JSON.parse(bytes(output, file, maximum));
const journal = (output, file, maximum) => {
    const value = bytes(output, file, maximum).toString('utf8').trim();
    return value ? value.split('\n').map(line => JSON.parse(line)) : [];
};

/** Any retained v6 identity, source, action stream or parent declaration requires all v6 checks. */
export function includesResizeEvidence(output, evidence, context, proof) {
    return context.resizeSuiteId !== undefined || context.ownedResizePolicy !== undefined
        || context.initialSettingsSha256 !== undefined || evidence.resizeEvidence !== undefined
        || Object.keys(resizeSources).some(key => context[key] !== undefined)
        || proof.proofs?.some(value => value.resize !== undefined) === true
        || present(join(output, 'resize-actions.jsonl')) || present(join(output, 'resize-snapshots.jsonl'));
}

/** The private fixture and retained source copies must agree with the recorded capability and exact hashes. */
function checkFixtureAndSources(output, context, marker) {
    check(context.resizeSuiteId === resizeSuiteId && context.ownedResizePolicy === ownedResizePolicy
        && marker?.policy === ownedResizePolicy && Array.isArray(marker.actions));
    const settingsBytes = bytes(output, 'initial-settings.json', 65536);
    check(hash(settingsBytes) === context.initialSettingsSha256);
    const settings = JSON.parse(settingsBytes);
    check(settings.AgentAccessObserveEnabled === true && settings.AgentAccessActEnabled === true
        && settings.SessionPersistence === 'Off' && settings.Profiles?.length === 1 && settings.Profiles[0].Type === 0);
    for (const [key, file] of Object.entries(resizeSources))
        check(/^[a-f\d]{64}$/.test(context[key] ?? '') && hash(bytes(output, join('sources', file), 2 * 1024 * 1024)) === context[key]);
}

/** Re-derive all smaller/restored states and the six source-selected App/kernel actions before finalizing. */
export function checkResizeEvidence(output, binding, runId, context, proof) {
    const marker = proof.proofs?.[0]?.resize;
    checkFixtureAndSources(output, context, marker);
    check(binding?.runId === runId && binding.suiteId === resizeSuiteId
        && ['resize-observations.json', 'resize-unavailable.json'].includes(binding.file));
    const reportBytes = bytes(output, binding.file);
    check(/^[a-f\d]{64}$/.test(binding.sha256 ?? '') && hash(reportBytes) === binding.sha256);
    const report = JSON.parse(reportBytes);
    check(report.runId === runId && report.suiteId === resizeSuiteId);
    const frames = journal(output, 'resize-snapshots.jsonl'), actions = journal(output, 'resize-actions.jsonl', 256 * 1024);
    check(same(marker.actions, actions));
    if (binding.file === 'resize-unavailable.json') {
        check(report.status === 'unavailable' && ['screenDetailsUnavailable', 'resizeGeometryUnavailable'].includes(report.reason)
            && report.fixtureStarted === false && report.plannedExperiments === 3 && frames.length === 0 && actions.length === 0
            && binding.status === 'unavailable' && binding.reason === report.reason && same(binding.counts, {}));
        return;
    }
    const derived = buildResizeReport(frames, actions, runId);
    check(same(report, derived) && marker.paneId === frames[0].paneId
        && same(marker.originalGeometry, { rows: frames[0].screen.rows, cols: frames[0].screen.cols }));
    const cell = json(output, 'cell-observations.json');
    check(cell.snapshots?.[0]?.paneId === marker.paneId && cell.snapshots[0].screen.rows === marker.originalGeometry.rows
        && cell.snapshots[0].screen.cols === marker.originalGeometry.cols);
    validateOutputGeometry(proof.proofs[0].terminalOutput.description, marker.originalGeometry.rows, marker.originalGeometry.cols);
    const counts = report.observations.reduce((values, item) => ({ ...values, [item.result]: (values[item.result] ?? 0) + 1 }), {});
    check(same(counts, binding.counts));
}
