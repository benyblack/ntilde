// Qualification of the exact terminfo.dev@4.0.0 case extensions.kitty-graphics.unicode-placeholders over the owned Kitty placeholder suite (ntilde-owned-kitty-placeholders-v1, #989) collected in a persistent
// (mux-backed, KeepOnClose, image policy 5) tab or in an ordinary tab. The carrier report is the authority for every stage; this projection adds the exact pinned-probe replay, one separately identified effect
// contract for placeholders, and explicit validity controls over the lineage (App process, multiplexer daemon, worker). The run has no stock collector, so there is no stock grade, and the pinned verdict, the
// effect verdict and any other result never merge.
import { createHash } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { directPlaceholdersScope, persistentPlaceholdersScope } from './collection-scope.mjs';
import { animationCasesByVersion, animationSuiteIds } from './kitty-animation-cases.mjs';
import { ownedAnimationPolicies } from './kitty-animation.mjs';
import { gradeEffect, kittyPlaceholdersQualificationIds, pinnedPlaceholdersSpanDigests, replayPinnedPlaceholdersProbe, upstreamBundleSha256, validityControls } from './kitty-animation-qualification.mjs';
import { directControls, persistentControls, verifyPersistentSuiteRun } from './kitty-animation-persistent-qualification.mjs';

export const placeholdersQualificationSuiteId = 'ntilde-owned-kitty-placeholders-persistent-qualification-v1';
export const placeholdersQualificationVersion = 1;
export const placeholdersReceiptSuiteId = 'ntilde-owned-kitty-placeholders-persistent-receipt-v1';
export const directPlaceholdersQualificationSuiteId = 'ntilde-owned-kitty-placeholders-direct-qualification-v1';
export const directPlaceholdersReceiptSuiteId = 'ntilde-owned-kitty-placeholders-direct-receipt-v1';
export { pinnedBundleArchive, upstreamBundleSha256 } from './kitty-animation-qualification.mjs';
export { kittyPlaceholdersQualificationIds, pinnedPlaceholdersSpanDigests } from './kitty-animation-qualification.mjs';
// Measured on the committed tree named here: the Release sidecar was built from the App at this commit and the measured scripts are its tree
// (later commits are publication, docs, test pins and merges of main; a merge of main can change VT sources the run captured, so the tree this publication ships in is not the measured App). The App tree is unchanged between the
// two commits: the sidecar was built at the first and the second only changed measured scripts.
export const measuredAppRevision = '8a2eb2952a7b965c101580093607d3f2681b7932';
export const measuredScriptsRevision = 'c46e42c786fc0646443b6d913771c86050b49da5';
// Both publications were collected on the same sidecar and the same scripts, one after the other.
export const directMeasuredAppRevision = '8a2eb2952a7b965c101580093607d3f2681b7932';
export const directMeasuredScriptsRevision = 'c46e42c786fc0646443b6d913771c86050b49da5';

const version = 5;
/** The recognized source policies whose graph carries the placeholder suite's sources (v70 is the first; v72 is the current one). An older graph does not carry them, so a downgraded one is refused rather than validated against its own (smaller) inventory, and an unrecognized later policy is refused too: it would fall back to a historical inventory that never lists those sources. Add a policy here when the graph version advances. */
export const placeholdersSourcePolicies = Object.freeze(['readonly-owned-source-graph-v70', 'readonly-owned-source-graph-v71', 'readonly-owned-source-graph-v72']);
/** The suite's own sources the measured graph must capture. */
const requiredSources = Object.freeze(['kittyAnimationInputsSourceSha256', 'kittyAnimationCasesSourceSha256', 'kittyAnimationOracleSourceSha256', 'kittyAnimationSuiteSourceSha256', 'kittyAnimationCaptureSourceSha256',
    'kittyAnimationCollectorSourceSha256', 'kittyAnimationReceiptSourceSha256']);
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const check = (ok, message) => { if (!ok) throw new Error(`kitty placeholders qualification: ${message}`); };
const suiteId = animationSuiteIds[version], policy = ownedAnimationPolicies[version];

/** The effect statement, for the kind of tab the suite ran in. */
const effectStatement = tab => `In ${tab}, Kitty Unicode placeholders behave as the Kitty protocol specifies, read by paired offscreen captures of one row of two cells: a transmitted 16x16 image (left half red, right half blue) with a virtual placement `
    + '(a=p,U=1) of a 2 x 1 grid draws nothing by itself; cells printing U+10EEEE whose foreground colour names the image, whose underline colour names the placement and whose row and column diacritics name tiles (0,0) and (0,1) show '
    + 'the red half and then the blue half, each tile an undistorted band of its own color; erasing the cells removes the image; the diacritics and not the screen position choose the tile (swapped columns swap the colors, '
    + 'and a lone cell naming column 0 shows the red half wherever it is printed); a bare placeholder after a cell with marks (row 0, column 0) continues in row 0 at the next column (inheritance of a nonzero row is not measured: the grid has one row); cells naming an image that was never transmitted, '
    + 'a placement that does not exist, or a tile outside the grid paint nothing while the valid cells (the positive control) do, and deleting the image\'s placements (a=d,d=i) makes those same cells paint nothing again. '
    + 'Every command that expects a Kitty reply returned OK (the virtual placement\'s reply echoes its placement id), and each clear passed its fence and left its target blank (a=d success is silent).';
const effectContractOf = (id, tab) => Object.freeze({ id, version: 1, carrierCases: Object.freeze(animationCasesByVersion[version].map(item => item.id)), statement: effectStatement(tab),
    primary: 'https://sw.kovidgoyal.net/kitty/graphics-protocol/#unicode-placeholders' });

/** Independently specified effect contracts of placeholders, one per kind of tab. They name the carrier cases they compose, never the pinned probe. */
export const placeholdersEffectContract = effectContractOf('ntilde-kitty-unicode-placeholders-persistent-effect-v1', 'a persistent tab (persistent image policy 5)');
export const directPlaceholdersEffectContract = effectContractOf('ntilde-kitty-unicode-placeholders-direct-effect-v1', 'an ordinary (non-persistent) tab');

const notMeasured = remote => 'a grid whose aspect differs from the image\'s (where the image sits inside the grid is not specified here), ids above 24 bits (the third diacritic), the inheritance of a nonzero row, relative placements (P=, Q=), scrolling, '
    + `history and reflow of placeholder cells, a palette or theme background behind the cells, text selection and copy, a second client, remote ${remote}sessions, other platforms`;
/** What differs between the two kinds of tab the suite is collected in. */
const variants = Object.freeze({
    persistent: Object.freeze({ scope: persistentPlaceholdersScope, receiptSuiteId: placeholdersReceiptSuiteId, qualificationSuiteId: placeholdersQualificationSuiteId, contract: placeholdersEffectContract, controls: persistentControls,
        persistent: true, noun: 'placeholders', evidence: 'owned-controlled-pixels-paired-raw-render-persistent-placeholders',
        scopeText: 'The Kitty Unicode placeholder suite alone in one persistent (KeepOnClose, image policy 5) tab on a disposable macOS sidecar: three cases (the tiles two cells show and erasing them, the tile the marks choose and the inheritance rule, '
            + 'and the cells that must paint nothing), over paired original offscreen PNGs and complete cells/modes. No stock collector ran, so there is no stock grade and no Core or common-harness claim; the pinned callbacks are replayed from the retained bundle '
            + `and stay inconclusive by design. Not measured: an ordinary (non-persistent) tab in this publication (see the ordinary-tab one), ${notMeasured('persistent ')}, a restart or reattach with placeholders on screen.` }),
    direct: Object.freeze({ scope: directPlaceholdersScope, receiptSuiteId: directPlaceholdersReceiptSuiteId, qualificationSuiteId: directPlaceholdersQualificationSuiteId, contract: directPlaceholdersEffectContract, controls: directControls,
        persistent: false, noun: 'ordinary-tab placeholders', evidence: 'owned-controlled-pixels-paired-raw-render-direct-placeholders',
        scopeText: 'The Kitty Unicode placeholder suite alone in one ordinary (non-persistent, SessionPersistence Off) tab on a disposable macOS sidecar, the worker the App\'s own child: three cases (the tiles two cells show and erasing them, the tile the marks choose and '
            + 'the inheritance rule, and the cells that must paint nothing), over paired original offscreen PNGs and complete cells/modes. No stock collector ran, so there is no stock grade and no Core or common-harness claim; the pinned callbacks are replayed from '
            + `the retained bundle and stay inconclusive by design. Not measured: a persistent tab in this publication (see the persistent one), ${notMeasured('')}.` }),
});

/**
 * The shared persistent-suite verification, then the placeholder suite's own: one App process, the very one the launch proof names, and no relaunch; every observation bound to the complete
 * identities the launch proof recorded for the App and the worker (process, parent, user, terminal, executable); and the source snapshot's removal bound to the tree this run measured and
 * dated after cleanup.
 */
export function verifyPlaceholdersRun(dir, kind = 'persistent') {
    const { scope, receiptSuiteId, noun } = variants[kind];
    return verifyPlaceholderSuiteRun(dir, { scope, receiptSuiteId, noun, version, suiteId, policy });
}

/** The same verification for any suite of the placeholder family (the grid suite uses it too): the suite's own version, ids and policy, and the collection scope and receipt of the kind of tab. */
export function verifyPlaceholderSuiteRun(dir, { scope, receiptSuiteId, noun, version, suiteId, policy }) {
    const recorded = JSON.parse(readFileSync(join(dir, 'context.json')));
    check(placeholdersSourcePolicies.includes(recorded.sourceSnapshotPolicy) && requiredSources.every(key => /^[a-f\d]{64}$/.test(recorded[key] ?? '')),
        `the run was not measured under a recognized source policy (${placeholdersSourcePolicies.join(', ')}) with the placeholder suite's sources captured`);
    const run = verifyPersistentSuiteRun(dir, { scope, receiptSuiteId, version, suiteId, policy, noun });
    const { original, context, proof, frames } = run;
    check(!original.relaunches && frames.every(frame => frame.ownership.app.pid === original.app.pid), 'a stage was observed under an App process other than the launched one, or the run relaunched it');
    const sameProcess = (seen, recorded) => ['pid', 'ppid', 'uid', 'tty', 'executable'].every(key => seen[key] === recorded[key]);
    check(original.app.executable === context.executable && frames.every(frame => sameProcess(frame.ownership.app, original.app) && sameProcess(frame.ownership.worker, original.worker)),
        'an observation is not bound to the launched App and its worker as the launch proof recorded them');
    const removal = proof.sourceSnapshot;
    check(removal.policy === context.sourceSnapshotPolicy && typeof removal.root === 'string' && removal.root.length > 0 && removal.root === context.sourceSnapshotRoot
        && !existsSync(removal.root) && Date.parse(removal.removedAt) >= Date.parse(proof.cleanedUpAt),
    'the source snapshot removal is not the one of the measured tree (a recorded, removed root), or predates cleanup');
    return run;
}

/** Validity controls specific to the placeholder suite, each re-checkable from the retained report. */
export function placeholdersControls({ proof, frames, report }, kind = 'persistent') {
    const { persistent } = variants[kind], daemon = proof.persistentDaemon;
    const stage = featureId => report.observations.find(item => item.featureId === featureId);
    const valid = stage('kitty.placeholders.unbound.valid'), build = stage('kitty.placeholders.tiles.build');
    return [
        ['one-app-process-throughout', new Set(frames.map(frame => frame.ownership.app.pid)).size === 1 && proof.proofs[0].relaunches === undefined,
            `the same App process observed every stage: nothing was relaunched, so every placeholder was read from the live ${persistent ? 'persistent' : 'ordinary'} pane`],
        ['same-session-throughout', frames.every(frame => frame.ownership.worker.pid === proof.proofs[0].worker.pid && (!persistent || frame.ownership.daemon.pid === daemon.identity.pid)),
            persistent ? 'every observation saw the same worker under the same multiplexer daemon' : 'every observation saw the same worker, the App\'s own child'],
        ['virtual-placement-drew-nothing', build?.result === 'passed' && build.classification.kind === 'background' && build.reply.statuses.length === 2 && build.reply.statuses.every(status => status === 'OK'),
            'the transmit and the virtual placement were acknowledged OK and the target was still blank afterwards: the image appears only on placeholder cells'],
        ['positive-control-after-negatives', valid?.result === 'passed' && JSON.stringify(valid.classification.tiles) === JSON.stringify(['red', 'blue']),
            'the valid cells painted both halves after the cells naming no image, no placement or a tile outside the grid had painted nothing, so those negatives are not an unset image or placement'],
    ].map(([name, held, detail]) => ({ name, held, detail }));
}

/** Project one extracted, verified placeholder run. `archive` is the retained pinned-bundle copy; `kind` is the tab the suite ran in. */
export async function placeholdersQualification(dir, source, archive, kind = 'persistent') {
    const { contract, controls, qualificationSuiteId, evidence, scopeText, persistent } = variants[kind];
    const verified = verifyPlaceholdersRun(dir, kind), { receipt, context, report, observationBytes } = verified;
    const validity = [...validityControls(report, { animationEvidence: receipt.animationEvidence, fixtureRemoved: receipt.fixtureRemoved }, context, observationBytes,
        { suiteId, policy, caseCount: animationCasesByVersion[version].length, requireTimed: false }), ...controls(verified), ...placeholdersControls(verified, kind)];
    check(validity.every(item => item.held), `validity control failed: ${validity.find(item => !item.held)?.name}`);
    const id = kittyPlaceholdersQualificationIds[0], pinned = (await replayPinnedPlaceholdersProbe(archive))[id], effect = gradeEffect(report, contract);
    const termless = pinned.termless, terminal = pinned.terminal, pinnedOutcome = termless.observation.outcome;
    const entry = { caseId: id, pinned: { pass: termless.pass, note: termless.note, observation: termless.observation, termless, terminal },
        effectContract: { id: contract.id, version: contract.version, statement: contract.statement, primary: contract.primary, carrierCases: contract.carrierCases, validity },
        contractResult: { outcome: effect.outcome, carrierCases: effect.cases.map(item => ({ caseId: item.caseId, result: item.result })) }, observations: effect.cases,
        ...(pinnedOutcome === effect.outcome ? {} : { divergence: { kind: 'pinned-inconclusive-by-design', pinnedOutcome, contractOutcome: effect.outcome } }),
        conclusion: { outcome: effect.outcome, grading: 'pinned-callbacks-preserved-plus-owned-placeholder-pixel-effect-v1', evidence, note: termless.note } };
    const suite = { schemaVersion: 1, suiteId: qualificationSuiteId, carrierSuiteId: suiteId, contractsVersion: placeholdersQualificationVersion,
        runId: context.runId, adapter: { id: context.adapter, upstreamBundleSha256, pinnedSpans: pinnedPlaceholdersSpanDigests }, scope: scopeText, cases: [entry] };
    const summary = { schemaVersion: 1, ownedRunId: context.runId, appRevision: source.appRevision, scriptsRevision: source.scriptsRevision, context, receipt,
        ...(persistent ? { daemon: verified.proof.persistentDaemon } : {}),
        pane: { id: verified.frames[0].paneId, stable: verified.frames.every(frame => frame.paneId === verified.frames[0].paneId) },
        carrier: { suiteId, policy, file: 'animation-observations.json', sha256: sha(observationBytes), counts: receipt.animationEvidence.counts,
            caseResults: report.caseResults.map(item => ({ caseId: item.caseId, result: item.result })) } };
    return { summary, report: suite };
}
