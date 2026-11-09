import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { inputSuiteId, ownedInputPolicy } from './input-cases.mjs';
import { buildInputReport, checkInput, sameInputFact } from './input-state.mjs';
import { capturedV9SourceFiles } from './owned-sources.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
export const inputSources = Object.freeze(Object.fromEntries(Object.entries(capturedV9SourceFiles).filter(([key]) => key.startsWith('input'))));
const files = ['input-snapshots.jsonl', 'input-actions.jsonl', 'input-observations.json', 'input-unavailable.json', 'input-failed-observation.json', 'input-raw-failure.json'];
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}
function bytes(output, file, limit = 16 * 1024 * 1024) {
    const path = join(output, file), stat = lstatSync(path); checkInput(stat.isFile() && !stat.isSymbolicLink() && stat.size <= limit);
    const value = readFileSync(path); checkInput(value.length <= limit); return value;
}
const json = (output, file) => JSON.parse(bytes(output, file));
const journal = (output, file) => bytes(output, file).toString().trim().split('\n').filter(Boolean).map(line => JSON.parse(line));
/** Coherent field/file stripping still cannot hide retained runner/worker imports of the new graph. */
export function includesInputEvidence(output, evidence, context, proof) {
    if (['readonly-owned-source-graph-v2', 'readonly-owned-source-graph-v3', 'readonly-owned-source-graph-v4', 'readonly-owned-source-graph-v5', 'readonly-owned-source-graph-v6', 'readonly-owned-source-graph-v7', 'readonly-owned-source-graph-v8', 'readonly-owned-source-graph-v9', 'readonly-owned-source-graph-v10', 'readonly-owned-source-graph-v11', 'readonly-owned-source-graph-v12', 'readonly-owned-source-graph-v13', 'readonly-owned-source-graph-v14', 'readonly-owned-source-graph-v15', 'readonly-owned-source-graph-v16', 'readonly-owned-source-graph-v17', 'readonly-owned-source-graph-v18', 'readonly-owned-source-graph-v19'].includes(context.sourceSnapshotPolicy) || context.inputSuiteId !== undefined
        || context.ownedInputPolicy !== undefined || evidence.inputEvidence !== undefined
        || Object.keys(inputSources).some(key => context[key] !== undefined)
        || proof.proofs?.some(value => value.input !== undefined) === true
        || ['readonly-owned-source-graph-v2', 'readonly-owned-source-graph-v3', 'readonly-owned-source-graph-v4', 'readonly-owned-source-graph-v5', 'readonly-owned-source-graph-v6', 'readonly-owned-source-graph-v7', 'readonly-owned-source-graph-v8', 'readonly-owned-source-graph-v9', 'readonly-owned-source-graph-v10', 'readonly-owned-source-graph-v11', 'readonly-owned-source-graph-v12', 'readonly-owned-source-graph-v13', 'readonly-owned-source-graph-v14', 'readonly-owned-source-graph-v15', 'readonly-owned-source-graph-v16', 'readonly-owned-source-graph-v17', 'readonly-owned-source-graph-v18', 'readonly-owned-source-graph-v19'].includes(proof.sourceSnapshot?.policy)
        || files.some(file => present(join(output, file)))) return true;
    const sourceDir = join(output, 'sources');
    if (!present(sourceDir)) return false;
    return readdirSync(sourceDir).some(file => file.startsWith('input-') || (file.endsWith('.mjs')
        && /(?:\bfrom|\bimport\s*\()\s*['"]\.\/(?:terminfo\/)?input-[a-z-]+\.mjs['"]/.test(bytes(output, join('sources', file), 2 * 1024 * 1024).toString())));
}
export function checkInputEvidence(output, binding, runId, context, proof) {
    const marker = proof.proofs?.[0]?.input;
    checkInput(context.inputSuiteId === inputSuiteId && context.ownedInputPolicy === ownedInputPolicy
        && ['readonly-owned-source-graph-v2', 'readonly-owned-source-graph-v3', 'readonly-owned-source-graph-v4', 'readonly-owned-source-graph-v5', 'readonly-owned-source-graph-v6', 'readonly-owned-source-graph-v7', 'readonly-owned-source-graph-v8', 'readonly-owned-source-graph-v9', 'readonly-owned-source-graph-v10', 'readonly-owned-source-graph-v11', 'readonly-owned-source-graph-v12', 'readonly-owned-source-graph-v13', 'readonly-owned-source-graph-v14', 'readonly-owned-source-graph-v15', 'readonly-owned-source-graph-v16', 'readonly-owned-source-graph-v17', 'readonly-owned-source-graph-v18', 'readonly-owned-source-graph-v19'].includes(context.sourceSnapshotPolicy) && marker?.policy === ownedInputPolicy
        && Array.isArray(marker.actions) && !present(join(output, 'input-failed-observation.json')) && !present(join(output, 'input-raw-failure.json')));
    for (const [key, file] of Object.entries(inputSources)) checkInput(/^[a-f\d]{64}$/.test(context[key] ?? '')
        && hash(bytes(output, join('sources', file), 2 * 1024 * 1024)) === context[key]);
    checkInput(binding?.runId === runId && binding.suiteId === inputSuiteId
        && ['input-observations.json', 'input-unavailable.json'].includes(binding.file)
        && !present(join(output, binding.file === 'input-observations.json' ? 'input-unavailable.json' : 'input-observations.json')));
    const reportBytes = bytes(output, binding.file); checkInput(hash(reportBytes) === binding.sha256);
    const report = JSON.parse(reportBytes), frames = journal(output, 'input-snapshots.jsonl'), actions = journal(output, 'input-actions.jsonl');
    checkInput(report.runId === runId && report.suiteId === inputSuiteId && sameInputFact(actions, marker.actions));
    if (binding.file === 'input-unavailable.json') {
        checkInput(report.schemaVersion === 1 && report.status === 'unavailable' && ['screenDetailsUnavailable', 'inputGeometryUnavailable'].includes(report.reason)
            && report.fixtureStarted === false && report.plannedExperiments === 39 && frames.length === 0 && actions.length === 0
            && marker.paneId === undefined && marker.geometry === undefined && marker.seal === undefined
            && binding.status === 'unavailable' && binding.reason === report.reason && sameInputFact(binding.counts, {}));
        return;
    }
    checkInput(binding.status === undefined && binding.reason === undefined);
    const derived = buildInputReport(frames, actions, marker.seal, runId);
    checkInput(sameInputFact(report, derived) && marker.paneId === frames[0].paneId
        && sameInputFact(marker.geometry, { rows: frames[0].screen.rows, cols: frames[0].screen.cols })
        && sameInputFact(binding.counts, { passed: 39 }));
    const original = proof.proofs[0];
    const sameProcess = (actual, expected) => ['pid', 'ppid', 'uid', 'tty', 'executable'].every(key => actual[key] === expected?.[key]);
    checkInput(actions.every(action => ['before', 'after'].every(side => sameProcess(action.observed[side].app, original.app)
        && sameProcess(action.observed[side].worker, original.worker))));
    const cell = json(output, 'cell-observations.json');
    checkInput(cell.snapshots?.[0]?.paneId === marker.paneId && cell.snapshots[0].screen.rows === marker.geometry.rows
        && cell.snapshots[0].screen.cols === marker.geometry.cols);
    const settingsBytes = bytes(output, 'initial-settings.json'); checkInput(hash(settingsBytes) === context.initialSettingsSha256);
    const settings = JSON.parse(settingsBytes);
    checkInput(settings.AgentAccessObserveEnabled === true && settings.AgentAccessActEnabled === true && settings.SessionPersistence === 'Off'
        && settings.Profiles?.length === 1 && settings.Profiles[0].Type === 0
        && typeof settings.Profiles[0].Name === 'string' && settings.Profiles[0].Name.length > 0
        && actions.every(action => ['before', 'after'].every(side => action.observed[side].pane.profileName === settings.Profiles[0].Name)));
}
