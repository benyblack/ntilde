// Qualification of the Kitty animation frame-composition suite (ntilde-owned-kitty-animation-compose-v1, #1027) collected in a persistent (mux-backed, KeepOnClose, image policy 4) tab or in an ordinary tab. The carrier
// report is the authority for every stage; this projection adds the exact pinned-probe replay, one separately identified effect contract for composition, and explicit validity controls over the
// lineage (App process, multiplexer daemon, worker). The run has no stock collector, so there is no stock grade, and the pinned verdict, the effect verdict and any other result never merge.
import { createHash } from 'node:crypto';
import { directComposeScope, persistentComposeScope } from './collection-scope.mjs';
import { animationCasesByVersion, animationSuiteIds } from './kitty-animation-cases.mjs';
import { ownedAnimationPolicies } from './kitty-animation.mjs';
import { gradeEffect, kittyAnimationQualificationIds, pinnedSpanDigests, replayPinnedAnimationProbe, upstreamBundleSha256, validityControls } from './kitty-animation-qualification.mjs';
import { directControls, persistentControls, verifyPersistentSuiteRun } from './kitty-animation-persistent-qualification.mjs';

export const composeQualificationSuiteId = 'ntilde-owned-kitty-animation-compose-qualification-v1';
export const composeQualificationVersion = 1;
export const composeReceiptSuiteId = 'ntilde-owned-kitty-animation-persistent-compose-receipt-v1';
export const directComposeQualificationSuiteId = 'ntilde-owned-kitty-animation-direct-compose-qualification-v1';
export const directComposeReceiptSuiteId = 'ntilde-owned-kitty-animation-direct-compose-receipt-v1';
export { pinnedBundleArchive, kittyAnimationQualificationIds, upstreamBundleSha256, pinnedSpanDigests } from './kitty-animation-qualification.mjs';
// Measured on the committed tree named here: the Release sidecar was built from the App at this commit and the measured scripts are its tree
// (later commits are publication, docs, test pins and merges of main only).
export const measuredAppRevision = '91b111e03dfbfba2a24bfcac85f6e05ede291182';
export const measuredScriptsRevision = 'a3404c55d55e67955ca212cd676ead81f7fe14fd';
// The ordinary-tab publication was measured on its own build and tree.
export const directMeasuredAppRevision = '91b111e03dfbfba2a24bfcac85f6e05ede291182';
export const directMeasuredScriptsRevision = 'a3404c55d55e67955ca212cd676ead81f7fe14fd';

const version = 4;
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const check = (ok, message) => { if (!ok) throw new Error(`kitty animation compose qualification: ${message}`); };
const suiteId = animationSuiteIds[version], policy = ownedAnimationPolicies[version];

/** The effect statement, for the kind of tab the suite ran in. */
const effectStatement = tab => `In ${tab}, a=c composes a whole source frame onto a destination frame of a retained image as the Kitty protocol specifies, read by paired offscreen captures: `
    + 'with C=1 the destination frame is replaced by the source frame\'s pixels, alpha included (a half-transparent green frame 4, built on no base, replaces the opaque blue frame 3, so the pane shows the green at its own alpha over the terminal background, '
    + 'where a blend would have shown it over the blue); by default the source is alpha-blended, so the same half-transparent frame blended onto the opaque red root shows the protocol\'s blend of the two; and compositions the protocol refuses '
    + '(a missing frame: ENOENT; a rectangle wider than the image, and one frame onto itself with overlapping rectangles: EINVAL) answer exactly those error codes and change nothing '
    + '(the displayed frame stays the root, and frame 2 is still its own green afterwards). Every other command that expects a Kitty reply returned OK, and each clear passed its fence and left its target blank (a=d success is silent).';
const effectContractOf = (id, tab) => Object.freeze({ id, version: 1, carrierCases: Object.freeze(animationCasesByVersion[version].map(item => item.id)), statement: effectStatement(tab),
    primary: 'https://sw.kovidgoyal.net/kitty/graphics-protocol/#composing-animation-frames' });

/** Independently specified effect contracts of frame composition, one per kind of tab. They name the carrier cases they compose, never the pinned probe. */
export const composeEffectContract = effectContractOf('ntilde-kitty-animation-persistent-compose-effect-v1', 'a persistent tab');
export const directComposeEffectContract = effectContractOf('ntilde-kitty-animation-direct-compose-effect-v1', 'an ordinary (non-persistent) tab, where the root\'s pixels are read back through the host decoder');

const notMeasured = remote => 'rectangle offsets and sizes smaller than the image (pinned by unit tests; a partial composition is not one uniform color), a composition source or destination that is a PNG-encoded root '
    + `(the root image is raw RGBA, f=32; only the added frame is PNG), a second client, remote ${remote}sessions, other platforms`;
/** What differs between the two kinds of tab the suite is collected in. */
const variants = Object.freeze({
    persistent: Object.freeze({ scope: persistentComposeScope, receiptSuiteId: composeReceiptSuiteId, qualificationSuiteId: composeQualificationSuiteId, contract: composeEffectContract, controls: persistentControls,
        persistent: true, noun: 'compose', evidence: 'owned-controlled-pixels-paired-raw-render-persistent-compose',
        scopeText: 'The Kitty animation frame-composition suite alone in one persistent (KeepOnClose, image policy 4) tab on a disposable macOS sidecar: two cases (whole-frame a=c compositions that replace and blend, and refused compositions that change nothing), '
            + 'over paired original offscreen PNGs and complete cells/modes. No stock collector ran, so there is no stock grade and no Core or common-harness claim; the pinned callbacks are replayed from the retained bundle and stay inconclusive by design. '
            + `Not measured: an ordinary (non-persistent) tab on the desktop in this publication (see the ordinary-tab one), ${notMeasured('persistent ')}, a restart or reattach after composing.` }),
    direct: Object.freeze({ scope: directComposeScope, receiptSuiteId: directComposeReceiptSuiteId, qualificationSuiteId: directComposeQualificationSuiteId, contract: directComposeEffectContract, controls: directControls,
        persistent: false, noun: 'ordinary-tab compose', evidence: 'owned-controlled-pixels-paired-raw-render-direct-compose',
        scopeText: 'The Kitty animation frame-composition suite alone in one ordinary (non-persistent, SessionPersistence Off) tab on a disposable macOS sidecar, the worker the App\'s own child: two cases (whole-frame a=c compositions that replace and blend, and refused compositions that change nothing), '
            + 'over paired original offscreen PNGs and complete cells/modes; the root\'s pixels are read back through the host decoder, the route a persistent session never takes. No stock collector ran, so there is no stock grade and no Core or common-harness claim; the pinned callbacks are replayed from the retained bundle and stay inconclusive by design. '
            + `Not measured: a persistent tab in this publication (see the persistent one), ${notMeasured('')}.` }),
});

/** The shared persistent-suite verification, then the compose suite's own: one App process, the very one the launch proof names, and no relaunch. */
export function verifyComposeRun(dir, kind = 'persistent') {
    const { scope, receiptSuiteId, noun } = variants[kind];
    const run = verifyPersistentSuiteRun(dir, { scope, receiptSuiteId, version, suiteId, policy, noun });
    check(!run.original.relaunches && run.frames.every(frame => frame.ownership.app.pid === run.original.app.pid), 'a stage was observed under an App process other than the launched one, or the run relaunched it');
    return run;
}

/** Validity controls specific to the composition suite, each re-checkable from the retained proof and report. */
export function composeControls({ proof, frames, report }, kind = 'persistent') {
    const { persistent } = variants[kind], daemon = proof.persistentDaemon, refuse = report.observations.find(item => item.featureId === 'kitty.animation.compose-refused.refuse');
    return [
        ['one-app-process-throughout', new Set(frames.map(frame => frame.ownership.app.pid)).size === 1 && proof.proofs[0].relaunches === undefined,
            `the same App process observed every stage: nothing was relaunched, so every composition ran in the live ${persistent ? 'persistent' : 'ordinary'} pane`],
        ['same-session-throughout', frames.every(frame => frame.ownership.worker.pid === proof.proofs[0].worker.pid && (!persistent || frame.ownership.daemon.pid === daemon.identity.pid)),
            persistent ? 'every observation saw the same worker under the same multiplexer daemon' : 'every observation saw the same worker, the App\'s own child'],
        ['refusals-were-the-named-errors', JSON.stringify(refuse?.reply?.statuses?.map(status => status.split(':')[0])) === JSON.stringify(['ENOENT', 'EINVAL', 'EINVAL']) && refuse.reply.passed === true,
            'the three refused compositions answered ENOENT, EINVAL and EINVAL in order, and nothing else'],
    ].map(([name, held, detail]) => ({ name, held, detail }));
}

/** Project one extracted, verified compose run. `archive` is the retained pinned-bundle copy; `kind` is the tab the suite ran in. */
export async function composeQualification(dir, source, archive, kind = 'persistent') {
    const { contract, controls, qualificationSuiteId, evidence, scopeText, persistent } = variants[kind];
    const verified = verifyComposeRun(dir, kind), { receipt, context, report, observationBytes } = verified;
    const validity = [...validityControls(report, { animationEvidence: receipt.animationEvidence, fixtureRemoved: receipt.fixtureRemoved }, context, observationBytes,
        { suiteId, policy, caseCount: animationCasesByVersion[version].length, requireTimed: false }), ...controls(verified), ...composeControls(verified, kind)];
    check(validity.every(item => item.held), `validity control failed: ${validity.find(item => !item.held)?.name}`);
    const id = kittyAnimationQualificationIds[0], pinned = (await replayPinnedAnimationProbe(archive))[id], effect = gradeEffect(report, contract);
    const termless = pinned.termless, terminal = pinned.terminal, pinnedOutcome = termless.observation.outcome;
    const entry = { caseId: id, pinned: { pass: termless.pass, note: termless.note, observation: termless.observation, termless, terminal },
        effectContract: { id: contract.id, version: contract.version, statement: contract.statement, primary: contract.primary, carrierCases: contract.carrierCases, validity },
        contractResult: { outcome: effect.outcome, carrierCases: effect.cases.map(item => ({ caseId: item.caseId, result: item.result })) }, observations: effect.cases,
        ...(pinnedOutcome === effect.outcome ? {} : { divergence: { kind: 'pinned-inconclusive-by-design', pinnedOutcome, contractOutcome: effect.outcome } }),
        conclusion: { outcome: effect.outcome, grading: 'pinned-callbacks-preserved-plus-owned-composition-pixel-effect-v1', evidence, note: termless.note } };
    const suite = { schemaVersion: 1, suiteId: qualificationSuiteId, carrierSuiteId: suiteId, contractsVersion: composeQualificationVersion,
        runId: context.runId, adapter: { id: context.adapter, upstreamBundleSha256, pinnedSpans: pinnedSpanDigests }, scope: scopeText, cases: [entry] };
    const summary = { schemaVersion: 1, ownedRunId: context.runId, appRevision: source.appRevision, scriptsRevision: source.scriptsRevision, context, receipt,
        ...(persistent ? { daemon: verified.proof.persistentDaemon } : {}),
        pane: { id: verified.frames[0].paneId, stable: verified.frames.every(frame => frame.paneId === verified.frames[0].paneId) },
        carrier: { suiteId, policy, file: 'animation-observations.json', sha256: sha(observationBytes), counts: receipt.animationEvidence.counts,
            caseResults: report.caseResults.map(item => ({ caseId: item.caseId, result: item.result })) } };
    return { summary, report: suite };
}
