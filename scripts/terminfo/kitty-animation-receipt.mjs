import { lstatSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { snapshotHash } from './agent-observer.mjs';
import { animationSuiteIds, animationCasesByVersion } from './kitty-animation-cases.mjs';
import { animationInputs, buildAnimationReport, ownedAnimationPolicies, animationVersionOfPolicy } from './kitty-animation.mjs';
import { checkImages, validateImageOwnership } from './static-images.mjs';
import { journal, json, present, read } from './static-images-receipt.mjs';
import { pngHash } from './pixels-capture.mjs';
import { isCellSizePolicy63 } from './owned-sources.mjs';

const policyNumber = context => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(context.sourceSnapshotPolicy ?? '')?.[1] ?? 0);
const files = ['animation-snapshots.jsonl', 'animation-actions.jsonl', 'animation-observations.json', 'animation-unavailable.json',
    'animation-failed-observation.json', 'animation-raw-failure.json', 'animation-inputs', 'animation-pixels'];
const nonces = frames => frames.filter(frame => frame.capture).map(frame => frame.capture.nonces.join(','));

/** Declared by the source policy, by any marker, or by retained artifacts: no field-stripping downgrade. */
export function includesAnimationEvidence(output, evidence, context, proof) {
    return (policyNumber(context) >= 63 && !isCellSizePolicy63(context)) || context.animationSuiteId !== undefined || context.ownedAnimationPolicy !== undefined
        || evidence.animationEvidence !== undefined || Object.keys(context).some(key => key.startsWith('kittyAnimation') && key.endsWith('SourceSha256'))
        || proof.proofs?.some(value => value.kittyAnimation !== undefined) === true || files.some(file => present(join(output, file)));
}

function checkUnavailable(output, bytes, binding, runId, frames, actions, marker, version) {
    const report = JSON.parse(bytes), cases = animationCasesByVersion[version];
    checkImages(report.schemaVersion === 1 && report.runId === runId && report.suiteId === animationSuiteIds[version]
        && report.status === 'unavailable' && ['screenDetailsUnavailable', 'imageGeometryUnavailable'].includes(report.reason)
        && report.fixtureStarted === false && report.plannedExperiments === cases.length && report.plannedStages === cases.reduce((total, item) => total + item.stages.length, 0)
        && frames.length === 0 && actions.length === 0 && marker.paneId === undefined && marker.geometry === undefined && marker.seal === undefined
        && binding.status === 'unavailable' && binding.reason === report.reason && snapshotHash(binding.counts) === snapshotHash({})
        && !present(join(output, 'animation-pixels')) && !present(join(output, 'animation-inputs')));
}

/** The retained input and pixel directories hold exactly the files the journal names, as real directories. */
function checkRetainedDirectories(output, frames, version) {
    for (const [directory, expected] of [['animation-inputs', animationInputs(version).map(value => value.file.split('/')[1])],
        ['animation-pixels', frames.flatMap(frame => frame.pixels.map(pixel => pixel.file.split('/')[1]))]]) {
        const stat = lstatSync(join(output, directory)); checkImages(stat.isDirectory() && !stat.isSymbolicLink());
        const members = readdirSync(join(output, directory)); checkImages(members.length === expected.length && members.every(file => expected.includes(file)));
    }
}

/** A retained run is rederived under the suite version its own context names: v1 (three cases) stays valid beside v2 (#1015). */
export function checkAnimationEvidence(output, binding, runId, context, proof) {
    const original = proof.proofs?.[0], marker = original?.kittyAnimation, version = animationVersionOfPolicy(context.ownedAnimationPolicy);
    checkImages(version !== null && policyNumber(context) >= 63 && context.animationSuiteId === animationSuiteIds[version] && context.ownedAnimationPolicy === ownedAnimationPolicies[version]
        && marker?.policy === ownedAnimationPolicies[version] && !present(join(output, 'animation-failed-observation.json')) && !present(join(output, 'animation-raw-failure.json')));
    checkImages(binding?.runId === runId && binding.suiteId === animationSuiteIds[version] && ['animation-observations.json', 'animation-unavailable.json'].includes(binding.file)
        && Object.keys(binding).every(key => ['runId', 'suiteId', 'file', 'sha256', 'counts', 'provisionalUntil', 'status', 'reason'].includes(key))
        && !present(join(output, binding.file === 'animation-observations.json' ? 'animation-unavailable.json' : 'animation-observations.json')));
    const bytes = read(output, binding.file); checkImages(pngHash(bytes) === binding.sha256);
    const frames = journal(output, 'animation-snapshots.jsonl'), actions = journal(output, 'animation-actions.jsonl');
    checkImages(snapshotHash(actions) === snapshotHash(marker.actions));
    if (binding.file === 'animation-unavailable.json') { checkUnavailable(output, bytes, binding, runId, frames, actions, marker, version); return; }
    checkImages(binding.status === undefined && binding.reason === undefined);
    const input = present(join(output, 'input-observations.json')) ? json(output, 'input-observations.json') : null;
    const images = present(join(output, 'image-snapshots.jsonl')) ? journal(output, 'image-snapshots.jsonl') : [];
    const derived = buildAnimationReport(frames, actions, marker.seal, runId, output, [...(input ? nonces(input.snapshots) : []), ...nonces(images)], version);
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
    checkRetainedDirectories(output, frames, version);
}
