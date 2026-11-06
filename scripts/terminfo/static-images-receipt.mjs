import { lstatSync, readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { snapshotHash } from './agent-observer.mjs';
import { staticImageSuiteId } from './static-images-cases.mjs';
import { buildStaticImageReport, checkImages, ownedStaticImagePolicy, imageInputs, validateImageOwnership } from './static-images.mjs';
import { pngHash } from './pixels-capture.mjs';
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}
const files = ['image-snapshots.jsonl', 'image-actions.jsonl', 'image-observations.json', 'image-unavailable.json', 'image-failed-observation.json', 'image-raw-failure.json', 'image-inputs', 'image-pixels'];
function read(output, file, limit = 16 * 1024 * 1024) {
    const stat = lstatSync(join(output, file)); checkImages(stat.isFile() && !stat.isSymbolicLink() && stat.size <= limit);
    const bytes = readFileSync(join(output, file)); checkImages(bytes.length <= limit); return bytes;
}
const json = (output, file) => JSON.parse(read(output, file));
const journal = (output, file) => read(output, file).toString().trim().split('\n').filter(Boolean).map(value => JSON.parse(value));
/** Retained imports and directories prevent a coherent downgrade to schema9 by field stripping. */
export function includesStaticImageEvidence(output, evidence, context, proof) {
    if (['readonly-owned-source-graph-v3', 'readonly-owned-source-graph-v4', 'readonly-owned-source-graph-v5', 'readonly-owned-source-graph-v6', 'readonly-owned-source-graph-v7'].includes(context.sourceSnapshotPolicy)
        || ['readonly-owned-source-graph-v3', 'readonly-owned-source-graph-v4', 'readonly-owned-source-graph-v5', 'readonly-owned-source-graph-v6', 'readonly-owned-source-graph-v7'].includes(proof.sourceSnapshot?.policy)
        || context.staticImageSuiteId !== undefined || context.ownedStaticImagePolicy !== undefined || evidence.staticImageEvidence !== undefined
        || Object.keys(context).some(key => key.startsWith('staticImage') && key.endsWith('SourceSha256'))
        || proof.proofs?.some(value => value.staticImages !== undefined) || files.some(file => present(join(output, file)))) return true;
    if (!present(join(output, 'sources'))) return false;
    return readdirSync(join(output, 'sources')).some(file => file.startsWith('static-images') || (file.endsWith('.mjs')
        && /(?:\bfrom|\bimport\s*\()\s*['"]\.\/(?:terminfo\/)?static-images(?:-[a-z-]+)?\.mjs['"]/.test(read(output, join('sources', file), 2 * 1024 * 1024).toString())));
}
export function checkStaticImageEvidence(output, binding, runId, context, proof) {
    const original = proof.proofs?.[0], marker = original?.staticImages;
    checkImages(['readonly-owned-source-graph-v3', 'readonly-owned-source-graph-v4', 'readonly-owned-source-graph-v5', 'readonly-owned-source-graph-v6', 'readonly-owned-source-graph-v7'].includes(context.sourceSnapshotPolicy) && context.staticImageSuiteId === staticImageSuiteId
        && context.ownedStaticImagePolicy === ownedStaticImagePolicy && marker?.policy === ownedStaticImagePolicy
        && !present(join(output, 'image-failed-observation.json')) && !present(join(output, 'image-raw-failure.json')));
    checkImages(binding?.runId === runId && binding.suiteId === staticImageSuiteId && ['image-observations.json', 'image-unavailable.json'].includes(binding.file)
        && Object.keys(binding).every(key => ['runId', 'suiteId', 'file', 'sha256', 'counts', 'provisionalUntil', 'status', 'reason'].includes(key))
        && !present(join(output, binding.file === 'image-observations.json' ? 'image-unavailable.json' : 'image-observations.json')));
    const bytes = read(output, binding.file); checkImages(pngHash(bytes) === binding.sha256);
    const frames = journal(output, 'image-snapshots.jsonl'), actions = journal(output, 'image-actions.jsonl');
    checkImages(snapshotHash(actions) === snapshotHash(marker.actions));
    if (binding.file === 'image-unavailable.json') {
        const report = JSON.parse(bytes);
        checkImages(report.schemaVersion === 1 && report.runId === runId && report.suiteId === staticImageSuiteId
            && report.status === 'unavailable' && ['screenDetailsUnavailable', 'imageGeometryUnavailable'].includes(report.reason)
            && report.fixtureStarted === false && report.plannedExperiments === 5 && report.plannedStages === 23
            && frames.length === 0 && actions.length === 0 && marker.paneId === undefined && marker.geometry === undefined && marker.seal === undefined
            && binding.status === 'unavailable' && binding.reason === report.reason && snapshotHash(binding.counts) === snapshotHash({})
            && !present(join(output, 'image-pixels')) && !present(join(output, 'image-inputs')));
        return;
    }
    checkImages(binding.status === undefined && binding.reason === undefined);
    const input = present(join(output, 'input-observations.json')) ? json(output, 'input-observations.json') : null;
    const prior = input ? input.snapshots.filter(frame => frame.capture).map(frame => frame.capture.nonces.join(',')) : [];
    const derived = buildStaticImageReport(frames, actions, marker.seal, runId, output, prior);
    checkImages(Date.parse(marker.seal.ownership.at) <= Date.parse(proof.cleanedUpAt));
    checkImages(snapshotHash(json(output, binding.file)) === snapshotHash(derived) && marker.paneId === frames[0].paneId
        && snapshotHash(marker.geometry) === snapshotHash({ rows: frames[0].screen.rows, cols: frames[0].screen.cols }));
    for (const frame of frames) validateImageOwnership(frame.ownership, { ...original, at: frame.ownership.at }, marker.geometry);
    const cell = json(output, 'cell-observations.json');
    checkImages(cell.snapshots[0].paneId === marker.paneId && cell.snapshots[0].screen.rows === marker.geometry.rows
        && cell.snapshots[0].screen.cols === marker.geometry.cols);
    const counts = {};
    for (const item of derived.observations) counts[item.result] = (counts[item.result] ?? 0) + 1;
    checkImages(snapshotHash(counts) === snapshotHash(binding.counts));
    for (const [directory, expected] of [['image-inputs', imageInputs().map(input => input.file.split('/')[1])],
        ['image-pixels', frames.flatMap(frame => frame.pixels.map(pixel => pixel.file.split('/')[1]))]]) {
        const stat = lstatSync(join(output, directory)); checkImages(stat.isDirectory() && !stat.isSymbolicLink());
        const members = readdirSync(join(output, directory)); checkImages(members.length === expected.length && members.every(file => expected.includes(file)));
    }
}
