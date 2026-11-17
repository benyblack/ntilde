// Qualification of the Kitty animation suite (ntilde-owned-kitty-animation-v2) collected in a persistent (mux-backed, KeepOnClose) tab (#1025).
// The carrier report is the authority for every stage; this projection adds the exact pinned-probe replay, one separately identified effect
// contract for the persistent tab, and explicit validity controls over the lineage (App, multiplexer daemon, worker). The run is not a full owned
// collection: no stock collector ran, so there is no stock grade, and the pinned verdict, the effect verdict and any other result never merge.
import { createHash } from 'node:crypto';
import { lstatSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { isDeepStrictEqual } from 'node:util';
import { snapshotHash } from './agent-observer.mjs';
import { persistentAnimationScope } from './collection-scope.mjs';
import { animationInputs, buildAnimationReport } from './kitty-animation.mjs';
import { effectContract, gradeEffect, kittyAnimationCarrierPolicy, kittyAnimationCarrierSuiteId, kittyAnimationQualificationIds, pinnedBundleArchive,
    pinnedSpanDigests, replayPinnedAnimationProbe, upstreamBundleSha256, validityControls } from './kitty-animation-qualification.mjs';
import { retainedInventory } from './metadata-receipt.mjs';
import { pngHash } from './pixels-capture.mjs';
import { checkImages, validateImageOwnership } from './static-images.mjs';
import { journal, json, present, read } from './static-images-receipt.mjs';

export const persistentAnimationQualificationSuiteId = 'ntilde-owned-kitty-animation-persistent-qualification-v1';
export const persistentAnimationQualificationVersion = 1;
export const persistentAnimationReceiptSuiteId = 'ntilde-owned-kitty-animation-persistent-receipt-v1';
export { pinnedBundleArchive, kittyAnimationQualificationIds, upstreamBundleSha256, pinnedSpanDigests } from './kitty-animation-qualification.mjs';
// Measured on the committed tree named here: the Release sidecar was built from the App at this commit and the measured scripts are its tree
// (later commits are publication, docs, test pins and merges of main only).
export const measuredAppRevision = '39fac4de7ec4a09d8ae3ae6236abdf29e6d29b9f';
export const measuredScriptsRevision = '39fac4de7ec4a09d8ae3ae6236abdf29e6d29b9f';

const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const check = (ok, message) => { if (!ok) throw new Error(`persistent kitty animation qualification: ${message}`); };
const version = 2;

/** Independently specified effect contract of the persistent tab. It names the carrier cases it composes, never the pinned probe. */
export const persistentEffectContract = Object.freeze({
    id: 'ntilde-kitty-animation-persistent-playback-effect-v1', version: 1,
    carrierCases: effectContract.carrierCases,
    statement: 'In a persistent tab, whose shell runs in the multiplexer daemon and whose pane is a client of it, the same animation behaviour as in a direct tab: ' + effectContract.statement,
    primary: effectContract.primary,
});

/** The retained carrier input and pixel directories hold exactly the files the journal names, as real directories. */
function checkRetainedDirectories(dir, frames) {
    for (const [directory, expected] of [['animation-inputs', animationInputs(version).map(value => value.file.split('/')[1])],
        ['animation-pixels', frames.flatMap(frame => frame.pixels.map(pixel => pixel.file.split('/')[1]))]]) {
        const stat = lstatSync(join(dir, directory)); checkImages(stat.isDirectory() && !stat.isSymbolicLink());
        const members = readdirSync(join(dir, directory)); checkImages(members.length === expected.length && members.every(file => expected.includes(file)));
    }
}

/**
 * The measured sources are the launcher's captured set: the retained manifest is the one the context names (by digest), under the context's own policy,
 * covers exactly the policy's inventory, and each retained source is the file that manifest and the context both digest.
 */
function checkSources(dir, context) {
    const inventory = retainedInventory(dir, context), manifestBytes = read(dir, 'source-snapshot.json'), manifest = JSON.parse(manifestBytes);
    check(sha(manifestBytes) === context.sourceSnapshotManifestSha256 && manifest.schemaVersion === 1 && manifest.policy === context.sourceSnapshotPolicy,
        'the retained source manifest is not the one the run names');
    check(snapshotHash(Object.keys(manifest.hashes ?? {}).sort((a, b) => a.localeCompare(b))) === snapshotHash(Object.keys(inventory).sort((a, b) => a.localeCompare(b))), 'the source manifest does not cover the policy inventory');
    for (const [key, file] of Object.entries(inventory)) {
        const digest = sha(read(dir, join('sources', file)));
        check(digest === context[key] && digest === manifest.hashes[key], `retained source ${file} differs from the measured digest`);
    }
}

/** The recorded multiplexer daemon is the executable serving the run's private root, started after the App, and the worker's parent. */
function checkDaemon(proof, context, frames) {
    const daemon = proof.persistentDaemon, original = proof.proofs?.[0];
    check(daemon?.identity && daemon.descriptor && daemon.appData, 'the ownership proof records no multiplexer daemon');
    check(daemon.command === `${context.executable} mux serve`, 'the daemon is not the launched executable serving');
    check(daemon.identity.executable === context.executable && daemon.descriptor.pid === daemon.identity.pid && daemon.descriptor.processName === 'Ntilde'
        && daemon.descriptor.endpoint === `${daemon.appData}/mux/mux.sock`, 'the descriptor does not name this daemon inside the private root');
    check(Date.parse(daemon.birth + ' UTC') >= Date.parse(daemon.appBirth + ' UTC') && daemon.appBirth === original?.privateClipboard?.appBirth, 'the daemon predates the owned App');
    check(original.worker.ppid === daemon.identity.pid && original.app.executable === context.executable && original.worker.uid === daemon.identity.uid,
        'the worker is not a child of the recorded daemon');
    for (const frame of frames) {
        const seen = frame.ownership.daemon;
        check(seen?.pid === daemon.identity.pid && seen.uid === daemon.identity.uid && seen.executable === daemon.identity.executable && frame.ownership.worker.ppid === seen.pid,
            'a frame was observed under another parent than the recorded daemon');
    }
}

/**
 * Re-derive everything a persistent animation run claims from its retained bytes: the receipt binding, the persistent settings, the measured
 * sources, the daemon lineage, the whole carrier report (from its journals) and the retained directories.
 */
export function verifyPersistentAnimationRun(dir) {
    const receipt = json(dir, 'persistent-animation-receipt.json'), context = json(dir, 'context.json'), proofBytes = read(dir, 'ownership-proof.json'), proof = JSON.parse(proofBytes);
    const observationBytes = read(dir, 'animation-observations.json'), settings = json(dir, 'initial-settings.json');
    const original = proof.proofs?.[0], marker = original?.kittyAnimation, binding = receipt.animationEvidence;
    check(receipt.schemaVersion === 1 && receipt.suiteId === persistentAnimationReceiptSuiteId && receipt.fixtureRemoved === true && receipt.runId === context.runId
        && /^[a-f\d]{32}$/.test(context.runId) && isDeepStrictEqual(receipt.collectionScope, persistentAnimationScope) && isDeepStrictEqual(context.collectionScope, persistentAnimationScope),
        'the receipt does not declare the persistent animation scope of this run');
    check(receipt.ownershipProofSha256 === sha(proofBytes) && receipt.contextSha256 === sha(read(dir, 'context.json')), 'the receipt does not bind the retained proof and context');
    check(settings.SessionPersistence === 'KeepOnClose' && context.initialSettingsSha256 === sha(read(dir, 'initial-settings.json')), 'the run was not configured for a persistent tab');
    check(proof.completed === true && proof.cleanedUpAt && !proof.cleanupError && proof.sourceSnapshot?.removedAt, 'the ownership proof has no completed cleanup');
    check(binding?.runId === context.runId && binding.suiteId === kittyAnimationCarrierSuiteId && binding.file === 'animation-observations.json' && binding.sha256 === pngHash(observationBytes),
        'the receipt does not bind the retained animation report');
    check(context.ownedAnimationPolicy === kittyAnimationCarrierPolicy && marker?.policy === kittyAnimationCarrierPolicy
        && !present(join(dir, 'animation-failed-observation.json')) && !present(join(dir, 'animation-unavailable.json')) && !present(join(dir, 'animation-raw-failure.json')),
        'the carrier did not run under the paired raw render policy v2 without failure');
    checkSources(dir, context);
    const frames = journal(dir, 'animation-snapshots.jsonl'), actions = journal(dir, 'animation-actions.jsonl'), report = JSON.parse(observationBytes);
    check(snapshotHash(actions) === snapshotHash(marker.actions), 'the journaled actions differ from the proof');
    const derived = buildAnimationReport(frames, actions, marker.seal, context.runId, dir, [], version);
    check(snapshotHash(report) === snapshotHash(derived) && marker.paneId === frames[0].paneId
        && snapshotHash(marker.geometry) === snapshotHash({ rows: frames[0].screen.rows, cols: frames[0].screen.cols }), 'the retained report does not re-derive from its journals');
    check(Date.parse(marker.seal.ownership.at) <= Date.parse(proof.cleanedUpAt), 'the seal postdates cleanup');
    checkDaemon(proof, context, frames);
    for (const frame of frames) validateImageOwnership(frame.ownership, { ...original, daemon: frames[0].ownership.daemon, at: frame.ownership.at }, marker.geometry);
    const counts = {};
    for (const item of derived.observations) counts[item.result] = (counts[item.result] ?? 0) + 1;
    check(snapshotHash(counts) === snapshotHash(binding.counts), 'the receipt counts differ from the re-derived stages');
    checkRetainedDirectories(dir, frames);
    return { receipt, context, proof, report, observationBytes, frames };
}

/** Validity controls specific to the persistent tab, each re-checkable from the retained proof. */
export function persistentControls({ context, proof, frames }) {
    const daemon = proof.persistentDaemon;
    return [
        ['persistent-tab-configured', context.collectionScope.persistence === 'KeepOnClose' && frames.length > 0,
            'the run set SessionPersistence KeepOnClose in its private settings, so the local pane runs in the multiplexer daemon'],
        ['worker-hosted-by-daemon', frames.every(frame => frame.ownership.worker.ppid === daemon.identity.pid && frame.ownership.worker.ppid !== frame.ownership.app.pid),
            'every observation proved the worker a child of the recorded daemon, never a direct child of the App'],
        ['daemon-is-the-launched-executable', daemon.command === `${context.executable} mux serve` && daemon.descriptor.endpoint === `${daemon.appData}/mux/mux.sock`,
            'the daemon is the launched sidecar serving this run\'s private app-data root, advertised through that root\'s descriptor'],
        ['daemon-stopped', proof.completed === true && Boolean(proof.cleanedUpAt) && !proof.cleanupError,
            'cleanup stopped the daemon and verified it gone before the receipt'],
    ].map(([name, held, detail]) => ({ name, held, detail }));
}

/** Project one extracted, verified persistent animation run. `archive` is the retained pinned-bundle copy. */
export async function persistentAnimationQualification(dir, source, archive) {
    const verified = verifyPersistentAnimationRun(dir), { receipt, context, report, observationBytes } = verified;
    const validity = [...validityControls(report, { animationEvidence: receipt.animationEvidence, fixtureRemoved: receipt.fixtureRemoved }, context, observationBytes), ...persistentControls(verified)];
    check(validity.every(item => item.held), `validity control failed: ${validity.find(item => !item.held)?.name}`);
    check(context.appVersionSource !== undefined && context.executableSha256 !== undefined, 'the App identity is not recorded');
    const id = kittyAnimationQualificationIds[0], pinned = (await replayPinnedAnimationProbe(archive))[id], effect = gradeEffect(report);
    const termless = pinned.termless, terminal = pinned.terminal, pinnedOutcome = termless.observation.outcome;
    const entry = { caseId: id, pinned: { pass: termless.pass, note: termless.note, observation: termless.observation, termless, terminal },
        effectContract: { id: persistentEffectContract.id, version: persistentEffectContract.version, statement: persistentEffectContract.statement, primary: persistentEffectContract.primary,
            carrierCases: persistentEffectContract.carrierCases, validity },
        contractResult: { outcome: effect.outcome, carrierCases: effect.cases.map(item => ({ caseId: item.caseId, result: item.result })) }, observations: effect.cases,
        ...(pinnedOutcome === effect.outcome ? {} : { divergence: { kind: 'pinned-inconclusive-by-design', pinnedOutcome, contractOutcome: effect.outcome } }),
        conclusion: { outcome: effect.outcome, grading: 'pinned-callbacks-preserved-plus-owned-wall-clock-pixel-effect-in-a-persistent-tab-v1', evidence: 'owned-controlled-pixels-paired-raw-render-persistent-tab', note: termless.note } };
    const suite = { schemaVersion: 1, suiteId: persistentAnimationQualificationSuiteId, carrierSuiteId: kittyAnimationCarrierSuiteId, contractsVersion: persistentAnimationQualificationVersion,
        runId: context.runId, adapter: { id: context.adapter, upstreamBundleSha256, pinnedSpans: pinnedSpanDigests },
        scope: 'The Kitty animation carrier of the owned suite, collected alone in one persistent (KeepOnClose) tab on a disposable macOS sidecar: the pane is a client of the multiplexer daemon and the worker is the daemon\'s child. '
            + 'The same four bounded cases as the direct-tab publication (stationary control, playback, stop-freeze, PNG and root-based frames) over paired original offscreen PNGs, complete cells/modes and wall-clock capture spans. '
            + 'No stock collector ran in this run, so there is no stock grade and no Core or common-harness claim; the pinned callbacks are replayed from the retained bundle and stay inconclusive by design. '
            + 'Not measured: a reattach or restart (restore from a snapshot), a second client, remote persistent sessions, other platforms, a non-PNG root, frame-rate or display-refresh cadence.',
        cases: [entry] };
    const summary = { schemaVersion: 1, ownedRunId: context.runId, appRevision: source.appRevision, scriptsRevision: source.scriptsRevision, context, receipt,
        daemon: verified.proof.persistentDaemon, carrier: { suiteId: kittyAnimationCarrierSuiteId, policy: kittyAnimationCarrierPolicy, file: 'animation-observations.json', sha256: sha(observationBytes),
            counts: receipt.animationEvidence.counts, caseResults: report.caseResults.map(item => ({ caseId: item.caseId, result: item.result })) } };
    return { summary, report: suite };
}
