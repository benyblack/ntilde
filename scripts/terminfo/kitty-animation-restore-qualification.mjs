// Qualification of the Kitty animation restore suite (ntilde-owned-kitty-animation-restore-v1) collected in a persistent (mux-backed, KeepOnClose) tab whose App
// is terminated with SIGTERM and started again on the same persistent session between stages (#1030). The carrier report is the authority for every stage; this projection adds the exact
// pinned-probe replay, one separately identified effect contract for the restore, and explicit validity controls over the lineage (App processes, multiplexer daemon,
// worker). The run has no stock collector, so there is no stock grade, and the pinned verdict, the effect verdict and any other result never merge.
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { isDeepStrictEqual } from 'node:util';
import { snapshotHash } from './agent-observer.mjs';
import { persistentRestoreScope } from './collection-scope.mjs';
import { animationCasesByVersion, animationSuiteIds } from './kitty-animation-cases.mjs';
import { buildAnimationReport, ownedAnimationPolicies } from './kitty-animation.mjs';
import { gradeEffect, kittyAnimationQualificationIds, pinnedSpanDigests, replayPinnedAnimationProbe, upstreamBundleSha256, validityControls } from './kitty-animation-qualification.mjs';
import { checkDaemon, checkRetainedDirectories, checkSources, persistentControls } from './kitty-animation-persistent-qualification.mjs';
import { pngHash } from './pixels-capture.mjs';
import { checkImages } from './static-images.mjs';
import { journal, json, present, read } from './static-images-receipt.mjs';

export const restoreQualificationSuiteId = 'ntilde-owned-kitty-animation-restore-qualification-v1';
export const restoreQualificationVersion = 1;
export const restoreReceiptSuiteId = 'ntilde-owned-kitty-animation-persistent-restore-receipt-v1';
export { pinnedBundleArchive, kittyAnimationQualificationIds, upstreamBundleSha256, pinnedSpanDigests } from './kitty-animation-qualification.mjs';
// Measured on the committed tree named here: the Release sidecar was built from the App at this commit and the measured scripts are its tree
// (later commits are publication, docs, test pins and merges of main only).
export const measuredAppRevision = '601cd0514a515383b33a27435d5dcb2f3183a9fd';
export const measuredScriptsRevision = '601cd0514a515383b33a27435d5dcb2f3183a9fd';

const version = 3, relaunchCount = 3;
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const check = (ok, message) => { if (!ok) throw new Error(`kitty animation restore qualification: ${message}`); };
const suiteId = animationSuiteIds[version], policy = ownedAnimationPolicies[version];

/** Independently specified effect contract of the restore. It names the carrier cases it composes, never the pinned probe. */
export const restoreEffectContract = Object.freeze({
    id: 'ntilde-kitty-animation-persistent-restore-effect-v1', version: 1,
    carrierCases: Object.freeze(animationCasesByVersion[version].map(item => item.id)),
    statement: 'In a persistent tab, after the App is terminated with SIGTERM and started again on the same persistent session (the daemon and the shell keep running), the pane shows the animation state that '
        + 'was in effect, read by paired offscreen captures against the App\'s wall clock: a running animation shows the frame its original start time gives (the root, then the third frame at about 12.5 s, '
        + 'the root again at about 17.5 s), not one restarted from the moment the App came back; an animation stopped on its second frame stays on it past a hold that would have advanced it; and '
        + 'edited frames survive: the root edited by a=f,r=1 still shows its edit, and a half-transparent PNG frame built over it afterwards shows the protocol\'s blend of the edited pixels. '
        + 'Every write is acknowledged OK and every cleared target is blank again.',
    primary: 'https://sw.kovidgoyal.net/kitty/graphics-protocol/#animation',
});

/** The relaunches the proof records form one chain of App processes: each starts from the previous one's pid, in order, after it, and every observed frame belongs to one of them. */
function checkRelaunches(proof, frames) {
    const first = proof.proofs[0], chain = first.relaunches;
    check(Array.isArray(chain) && chain.length === relaunchCount, `the proof records ${chain?.length} relaunches, not ${relaunchCount}`);
    let previous = first.app.pid, previousAt = Date.parse(first.at);
    for (const item of chain) {
        check(item.from === previous && Number.isSafeInteger(item.to) && item.to > 1 && item.to !== item.from && Date.parse(item.at) >= previousAt && Date.parse(item.appBirth + ' UTC') >= Date.parse(first.privateClipboard.appBirth + ' UTC'),
            'the relaunch chain is not one ordered chain of App processes');
        previous = item.to; previousAt = Date.parse(item.at);
    }
    // Each relaunch finished before the first observation under its new App process, and every one before cleanup.
    const cleanedUpAt = Date.parse(proof.cleanedUpAt);
    for (const item of chain) {
        const firstFrame = frames.find(frame => frame.ownership.app.pid === item.to);
        check(Date.parse(item.startedAt) <= Date.parse(item.at) && Date.parse(item.at) <= Date.parse(firstFrame?.ownership.at) && Date.parse(item.at) <= cleanedUpAt,
            'a relaunch is not dated inside the run: it must finish before its new App was first observed and before cleanup');
    }
    const allowed = [first.app.pid, ...chain.map(item => item.to)];
    check(frames.every(frame => allowed.includes(frame.ownership.app.pid)), 'a frame was observed under an App process the proof does not record');
    check(snapshotHash([...new Set(frames.map(frame => frame.ownership.app.pid))]) === snapshotHash(allowed), 'an App process of the chain observed nothing, or one was observed out of order');
    const reattached = frames.filter(frame => frame.stageId === 'reattach').map(frame => frame.ownership.app.pid);
    check(snapshotHash(reattached) === snapshotHash(chain.map(item => item.to)), 'the first frame under each new App process is not its reattach stage');
}

/**
 * Re-derive everything a restore run claims from its retained bytes: the receipt binding, the persistent settings, the measured sources, the chain of App
 * processes and the daemon lineage, the whole carrier report (from its journals, with every relaunch epoch) and the retained directories.
 */
export function verifyRestoreRun(dir) {
    const receipt = json(dir, 'persistent-animation-receipt.json'), context = json(dir, 'context.json'), proofBytes = read(dir, 'ownership-proof.json'), proof = JSON.parse(proofBytes);
    const observationBytes = read(dir, 'animation-observations.json'), settings = json(dir, 'initial-settings.json');
    const original = proof.proofs?.[0], marker = original?.kittyAnimation, binding = receipt.animationEvidence;
    check(receipt.schemaVersion === 1 && receipt.suiteId === restoreReceiptSuiteId && receipt.fixtureRemoved === true && receipt.runId === context.runId
        && /^[a-f\d]{32}$/.test(context.runId) && isDeepStrictEqual(receipt.collectionScope, persistentRestoreScope) && isDeepStrictEqual(context.collectionScope, persistentRestoreScope),
        'the receipt does not declare the persistent restore scope of this run');
    check(receipt.ownershipProofSha256 === sha(proofBytes) && receipt.contextSha256 === sha(read(dir, 'context.json')), 'the receipt does not bind the retained proof and context');
    check(settings.SessionPersistence === 'KeepOnClose' && context.initialSettingsSha256 === sha(read(dir, 'initial-settings.json')), 'the run was not configured for a persistent tab');
    check(proof.completed === true && proof.cleanedUpAt && !proof.cleanupError && proof.sourceSnapshot?.removedAt, 'the ownership proof has no completed cleanup');
    check(binding?.runId === context.runId && binding.suiteId === suiteId && binding.file === 'animation-observations.json' && binding.sha256 === pngHash(observationBytes),
        'the receipt does not bind the retained animation report');
    check(context.animationSuiteId === suiteId && context.ownedAnimationPolicy === policy && marker?.policy === policy
        && !present(join(dir, 'animation-failed-observation.json')) && !present(join(dir, 'animation-unavailable.json')) && !present(join(dir, 'animation-raw-failure.json')),
        'the carrier did not run the restore suite under its paired raw render policy without failure');
    checkSources(dir, context);
    const frames = journal(dir, 'animation-snapshots.jsonl'), actions = journal(dir, 'animation-actions.jsonl'), report = JSON.parse(observationBytes);
    check(snapshotHash(actions) === snapshotHash(marker.actions), 'the journaled actions differ from the proof');
    const derived = buildAnimationReport(frames, actions, marker.seal, context.runId, dir, [], version);
    check(snapshotHash(report) === snapshotHash(derived) && snapshotHash(marker.geometry) === snapshotHash({ rows: frames[0].screen.rows, cols: frames[0].screen.cols }),
        'the retained report does not re-derive from its journals');
    check(marker.paneId === frames[0].paneId, 'the recorded pane is not the first App\'s pane');
    check(Date.parse(marker.seal.ownership.at) <= Date.parse(proof.cleanedUpAt), 'the seal postdates cleanup');
    checkDaemon(proof, context, frames);
    checkRelaunches(proof, frames);
    const counts = {};
    for (const item of derived.observations) counts[item.result] = (counts[item.result] ?? 0) + 1;
    check(snapshotHash(counts) === snapshotHash(binding.counts), 'the receipt counts differ from the re-derived stages');
    checkRetainedDirectories(dir, frames, version);
    checkImages(frames.length === derived.snapshots.length);
    return { receipt, context, proof, report, observationBytes, frames };
}

/** Validity controls specific to the restore, each re-checkable from the retained proof. */
export function restoreControls({ proof, frames }) {
    const chain = proof.proofs[0].relaunches, daemon = proof.persistentDaemon;
    return [
        ['three-relaunches-one-chain', chain.length === relaunchCount && chain.every((item, index) => item.from === (index ? chain[index - 1].to : proof.proofs[0].app.pid)),
            'the App was terminated with SIGTERM and started again three times, each new process starting from the previous one'],
        ['same-session-reattached', frames.every(frame => frame.ownership.worker.pid === proof.proofs[0].worker.pid && frame.ownership.daemon.pid === daemon.identity.pid),
            'every observation, before and after each relaunch, saw the same worker under the same multiplexer daemon: no new shell was spawned'],
        ['restored-pane-matches-bound-state', frames.filter(frame => frame.stageId === 'reattach').every(frame => frame.sha256 === frame.afterSha256),
            'each reattached pane showed a stable screen before and after its captures (the carrier also compared it with the bound screen)'],
        ['each-relaunch-reattached-by-the-next-stage', frames.filter(frame => frame.stageId === 'reattach').length === relaunchCount,
            'each relaunch was followed by a read of the reattached pane before its next write'],
    ].map(([name, held, detail]) => ({ name, held, detail }));
}

/** Project one extracted, verified restore run. `archive` is the retained pinned-bundle copy. */
export async function restoreQualification(dir, source, archive) {
    const verified = verifyRestoreRun(dir), { receipt, context, report, observationBytes } = verified;
    const validity = [...validityControls(report, { animationEvidence: receipt.animationEvidence, fixtureRemoved: receipt.fixtureRemoved }, context, observationBytes,
        { suiteId, policy, caseCount: animationCasesByVersion[version].length }), ...persistentControls(verified), ...restoreControls(verified)];
    check(validity.every(item => item.held), `validity control failed: ${validity.find(item => !item.held)?.name}`);
    const id = kittyAnimationQualificationIds[0], pinned = (await replayPinnedAnimationProbe(archive))[id], effect = gradeEffect(report, restoreEffectContract);
    const termless = pinned.termless, terminal = pinned.terminal, pinnedOutcome = termless.observation.outcome;
    const entry = { caseId: id, pinned: { pass: termless.pass, note: termless.note, observation: termless.observation, termless, terminal },
        effectContract: { id: restoreEffectContract.id, version: restoreEffectContract.version, statement: restoreEffectContract.statement, primary: restoreEffectContract.primary,
            carrierCases: restoreEffectContract.carrierCases, validity },
        contractResult: { outcome: effect.outcome, carrierCases: effect.cases.map(item => ({ caseId: item.caseId, result: item.result })) }, observations: effect.cases,
        ...(pinnedOutcome === effect.outcome ? {} : { divergence: { kind: 'pinned-inconclusive-by-design', pinnedOutcome, contractOutcome: effect.outcome } }),
        conclusion: { outcome: effect.outcome, grading: 'pinned-callbacks-preserved-plus-owned-wall-clock-pixel-effect-after-relaunch-v1', evidence: 'owned-controlled-pixels-paired-raw-render-persistent-restore', note: termless.note } };
    const suite = { schemaVersion: 1, suiteId: restoreQualificationSuiteId, carrierSuiteId: suiteId, contractsVersion: restoreQualificationVersion,
        runId: context.runId, adapter: { id: context.adapter, upstreamBundleSha256, pinnedSpans: pinnedSpanDigests },
        scope: 'The Kitty animation restore suite alone in one persistent (KeepOnClose) tab on a disposable macOS sidecar: three cases (a running animation, a stopped one, and PNG and root-edited frames), each read again after the App process '
            + 'was killed and started again on the same private root while the multiplexer daemon and the shell kept running, so the pane reattached the same session from the daemon\'s snapshot (the App saves its session after each attach, as it would after a crash). '
            + 'Over paired original offscreen PNGs, complete cells/modes and wall-clock capture spans. No stock collector ran, so there is no stock grade and no Core or common-harness claim; the pinned callbacks are replayed from the retained bundle and stay inconclusive by design. '
            + 'Not measured: a graceful quit (the session is saved by the App\'s own attach-time save, not its shutdown path), a second client, remote persistent sessions, other platforms, a non-PNG root, frame-rate or display-refresh cadence.',
        cases: [entry] };
    const summary = { schemaVersion: 1, ownedRunId: context.runId, appRevision: source.appRevision, scriptsRevision: source.scriptsRevision, context, receipt,
        daemon: verified.proof.persistentDaemon, relaunches: verified.proof.proofs[0].relaunches,
        pane: { id: verified.frames[0].paneId, sameAcrossRelaunches: verified.frames.every(frame => frame.paneId === verified.frames[0].paneId) },
        carrier: { suiteId, policy, file: 'animation-observations.json', sha256: sha(observationBytes), counts: receipt.animationEvidence.counts,
            caseResults: report.caseResults.map(item => ({ caseId: item.caseId, result: item.result })) } };
    return { summary, report: suite };
}
