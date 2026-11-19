// Qualification of the Kitty Unicode placeholder grid suite (ntilde-owned-kitty-placeholders-grid-v1, #989): the cases the first placeholder suite left unmeasured, collected in a persistent (image policy 5) tab or in an ordinary tab. It is
// a further measurement of the exact terminfo.dev@4.0.0 case extensions.kitty-graphics.unicode-placeholders: the carrier report is the authority for every stage, and this projection adds the exact pinned-probe replay, one separately
// identified effect contract and explicit validity controls over the lineage. The run has no stock collector, so there is no stock grade, and the pinned verdict and the effect verdict never merge.
import { createHash } from 'node:crypto';
import { directPlaceholderGridScope, persistentPlaceholderGridScope } from './collection-scope.mjs';
import { animationCasesByVersion, animationSuiteIds } from './kitty-animation-cases.mjs';
import { ownedAnimationPolicies } from './kitty-animation.mjs';
import { gradeEffect, kittyPlaceholdersQualificationIds, pinnedPlaceholdersSpanDigests, replayPinnedPlaceholdersProbe, upstreamBundleSha256, validityControls } from './kitty-animation-qualification.mjs';
import { directControls, persistentControls } from './kitty-animation-persistent-qualification.mjs';
import { verifyPlaceholderSuiteRun } from './kitty-placeholders-qualification.mjs';

export const placeholderGridQualificationSuiteId = 'ntilde-owned-kitty-placeholder-grid-persistent-qualification-v1';
export const placeholderGridQualificationVersion = 1;
export const placeholderGridReceiptSuiteId = 'ntilde-owned-kitty-placeholder-grid-persistent-receipt-v1';
export const directPlaceholderGridQualificationSuiteId = 'ntilde-owned-kitty-placeholder-grid-direct-qualification-v1';
export const directPlaceholderGridReceiptSuiteId = 'ntilde-owned-kitty-placeholder-grid-direct-receipt-v1';
export { pinnedBundleArchive, upstreamBundleSha256 } from './kitty-animation-qualification.mjs';
// Measured on the committed trees named here: the Release sidecar was built from the App at the first commit, and the measured scripts are the tree of the second (the commits between them only changed measured scripts, so the App tree is
// the same). Later commits are publication, docs and test pins, and merges of main (which can change VT sources the run captured, so the tree this publication ships in is not the measured App).
export const measuredAppRevision = 'b5e6cec9fde963cceffb56e2498c13b50af570d7';
export const measuredScriptsRevision = 'dc45f4cd241968a003919410899d3923769a6f2d';
// Both publications were collected on the same sidecar and the same scripts, one after the other.
export const directMeasuredAppRevision = measuredAppRevision;
export const directMeasuredScriptsRevision = measuredScriptsRevision;

const version = 6;
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const check = (ok, message) => { if (!ok) throw new Error(`kitty placeholder grid qualification: ${message}`); };
const suiteId = animationSuiteIds[version], policy = ownedAnimationPolicies[version];

/** The effect statement, for the kind of tab the suite ran in. */
const effectStatement = tab => `In ${tab}, Kitty Unicode placeholders behave as the Kitty protocol specifies over a grid of more than one row, read by paired offscreen captures of a 4 x 2 grid of cells: a transmitted 16x16 image in four colored `
    + 'quadrants (red and green above, blue and yellow below) with a virtual placement (a=p,U=1) of a 4 x 2 grid draws nothing by itself; cells whose row and column marks name the grid\'s tiles show the quadrant color of their tile (red red green green over '
    + 'blue blue yellow yellow); the row marks and not the screen row choose the tile (the rows swapped on screen swap their colors, and the lower tiles printed on the top screen row alone show blue and yellow there with nothing below); a placeholder '
    + 'without marks after a cell with marks (row 1, column 0 in the lower row) continues in the same row at the next column, so a nonzero row is inherited too; and tiles belong to the text that carries them: scrolling the screen up one line moves them up '
    + 'with their cells, erasing the lower text row removes the lower tiles and only them, and inserting a line above the grid moves them down. A line a terminal scrolls in or inserts holds either the active background or the default cell '
    + 'depending on the terminal, and the suite accepts both. Every command that expects a Kitty reply returned OK (the virtual placement\'s reply echoes its placement id), and each clear passed its fence and left its target blank.';
const effectContractOf = (id, tab) => Object.freeze({ id, version: 1, carrierCases: Object.freeze(animationCasesByVersion[version].map(item => item.id)), statement: effectStatement(tab),
    primary: 'https://sw.kovidgoyal.net/kitty/graphics-protocol/#unicode-placeholders' });

/** Independently specified effect contracts of the placeholder grid, one per kind of tab. They name the carrier cases they compose, never the pinned probe. */
export const placeholderGridEffectContract = effectContractOf('ntilde-kitty-unicode-placeholder-grid-persistent-effect-v1', 'a persistent tab (persistent image policy 5)');
export const directPlaceholderGridEffectContract = effectContractOf('ntilde-kitty-unicode-placeholder-grid-direct-effect-v1', 'an ordinary (non-persistent) tab');

const notMeasured = remote => 'a grid whose aspect differs from the image\'s (where the image sits inside the grid is not asserted: a cell is read where its quadrant is under every placement of the square), swapped columns on a grid, ids above 24 bits (the third diacritic), '
    + `relative placements (P=, Q=), resize and reflow of placeholder cells, a palette or theme background behind the cells, text selection and copy, a second client, remote ${remote}sessions, other platforms`;
/** What differs between the two kinds of tab the suite is collected in. */
const variants = Object.freeze({
    persistent: Object.freeze({ scope: persistentPlaceholderGridScope, receiptSuiteId: placeholderGridReceiptSuiteId, qualificationSuiteId: placeholderGridQualificationSuiteId, contract: placeholderGridEffectContract, controls: persistentControls,
        persistent: true, noun: 'placeholder grid', evidence: 'owned-controlled-pixels-paired-raw-render-persistent-placeholder-grid',
        scopeText: 'The Kitty Unicode placeholder grid suite alone in one persistent (KeepOnClose, image policy 5) tab on a disposable macOS sidecar: two cases (the tile rows, the row marks and the inheritance of a nonzero row on a 4 x 2 grid; and tiles that scroll, '
            + 'shrink and grow with their text rows), over paired original offscreen PNGs and complete cells/modes. No stock collector ran, so there is no stock grade and no Core or common-harness claim; the pinned callbacks are replayed from the retained bundle '
            + `and stay inconclusive by design. Not measured: an ordinary (non-persistent) tab in this publication (see the ordinary-tab one), ${notMeasured('persistent ')}, a restart or reattach with placeholders on screen.` }),
    direct: Object.freeze({ scope: directPlaceholderGridScope, receiptSuiteId: directPlaceholderGridReceiptSuiteId, qualificationSuiteId: directPlaceholderGridQualificationSuiteId, contract: directPlaceholderGridEffectContract, controls: directControls,
        persistent: false, noun: 'ordinary-tab placeholder grid', evidence: 'owned-controlled-pixels-paired-raw-render-direct-placeholder-grid',
        scopeText: 'The Kitty Unicode placeholder grid suite alone in one ordinary (non-persistent, SessionPersistence Off) tab on a disposable macOS sidecar, the worker the App\'s own child: two cases (the tile rows, the row marks and the inheritance of a nonzero row '
            + 'on a 4 x 2 grid; and tiles that scroll, shrink and grow with their text rows), over paired original offscreen PNGs and complete cells/modes. No stock collector ran, so there is no stock grade and no Core or common-harness claim; the pinned callbacks are replayed '
            + `from the retained bundle and stay inconclusive by design. Not measured: a persistent tab in this publication (see the persistent one), ${notMeasured('')}.` }),
});

/** The shared placeholder-suite verification of the grid suite's run (policy v70 with the suite's sources captured, the launch identities, the removed source snapshot, one App and no relaunch). */
export function verifyPlaceholderGridRun(dir, kind = 'persistent') {
    const { scope, receiptSuiteId, noun } = variants[kind];
    return verifyPlaceholderSuiteRun(dir, { scope, receiptSuiteId, noun, version, suiteId, policy });
}

/** Validity controls specific to the grid suite, each re-checkable from the retained report. */
export function placeholderGridControls({ proof, frames, report }, kind = 'persistent') {
    const { persistent } = variants[kind], daemon = proof.persistentDaemon;
    const stage = featureId => report.observations.find(item => item.featureId === featureId);
    const grid = stage('kitty.placeholders.rows.grid'), inherit = stage('kitty.placeholders.rows.inherit-rows'), build = stage('kitty.placeholders.rows.build');
    return [
        ['one-app-process-throughout', new Set(frames.map(frame => frame.ownership.app.pid)).size === 1 && proof.proofs[0].relaunches === undefined,
            `the same App process observed every stage: nothing was relaunched, so every placeholder was read from the live ${persistent ? 'persistent' : 'ordinary'} pane`],
        ['same-session-throughout', frames.every(frame => frame.ownership.worker.pid === proof.proofs[0].worker.pid && (!persistent || frame.ownership.daemon.pid === daemon.identity.pid)),
            persistent ? 'every observation saw the same worker under the same multiplexer daemon' : 'every observation saw the same worker, the App\'s own child'],
        ['virtual-placement-drew-nothing', build?.result === 'passed' && build.classification.kind === 'background' && build.reply.statuses.length === 2 && build.reply.statuses.every(status => status === 'OK'),
            'the transmit and the virtual placement of the 4 x 2 grid were acknowledged OK and the target was still blank afterwards'],
        ['whole-grid-painted', grid?.result === 'passed' && grid.classification.cells === 'rrgg/bbyy',
            'the cells with their own row and column marks showed all four quadrant colors in their places, so the image, the placement and the cells were all in effect before any variation'],
        ['nonzero-row-inherited-and-not-defaulted', inherit?.result === 'passed' && inherit.classification.cells === 'rrgg/bbyy',
            'a bare placeholder after a cell naming row 1 painted the lower tiles, which a terminal that defaulted a missing row to 0 would have painted as the upper ones'],
    ].map(([name, held, detail]) => ({ name, held, detail }));
}

/** Project one extracted, verified placeholder grid run. `archive` is the retained pinned-bundle copy; `kind` is the tab the suite ran in. */
export async function placeholderGridQualification(dir, source, archive, kind = 'persistent') {
    const { contract, controls, qualificationSuiteId, evidence, scopeText, persistent } = variants[kind];
    const verified = verifyPlaceholderGridRun(dir, kind), { receipt, context, report, observationBytes } = verified;
    const validity = [...validityControls(report, { animationEvidence: receipt.animationEvidence, fixtureRemoved: receipt.fixtureRemoved }, context, observationBytes,
        { suiteId, policy, caseCount: animationCasesByVersion[version].length, requireTimed: false }), ...controls(verified), ...placeholderGridControls(verified, kind)];
    check(validity.every(item => item.held), `validity control failed: ${validity.find(item => !item.held)?.name}`);
    const id = kittyPlaceholdersQualificationIds[0], pinned = (await replayPinnedPlaceholdersProbe(archive))[id], effect = gradeEffect(report, contract);
    const termless = pinned.termless, terminal = pinned.terminal, pinnedOutcome = termless.observation.outcome;
    const entry = { caseId: id, pinned: { pass: termless.pass, note: termless.note, observation: termless.observation, termless, terminal },
        effectContract: { id: contract.id, version: contract.version, statement: contract.statement, primary: contract.primary, carrierCases: contract.carrierCases, validity },
        contractResult: { outcome: effect.outcome, carrierCases: effect.cases.map(item => ({ caseId: item.caseId, result: item.result })) }, observations: effect.cases,
        ...(pinnedOutcome === effect.outcome ? {} : { divergence: { kind: 'pinned-inconclusive-by-design', pinnedOutcome, contractOutcome: effect.outcome } }),
        conclusion: { outcome: effect.outcome, grading: 'pinned-callbacks-preserved-plus-owned-placeholder-grid-pixel-effect-v1', evidence, note: termless.note } };
    const suite = { schemaVersion: 1, suiteId: qualificationSuiteId, carrierSuiteId: suiteId, contractsVersion: placeholderGridQualificationVersion,
        runId: context.runId, adapter: { id: context.adapter, upstreamBundleSha256, pinnedSpans: pinnedPlaceholdersSpanDigests }, scope: scopeText, cases: [entry] };
    const summary = { schemaVersion: 1, ownedRunId: context.runId, appRevision: source.appRevision, scriptsRevision: source.scriptsRevision, context, receipt,
        ...(persistent ? { daemon: verified.proof.persistentDaemon } : {}),
        pane: { id: verified.frames[0].paneId, stable: verified.frames.every(frame => frame.paneId === verified.frames[0].paneId) },
        carrier: { suiteId, policy, file: 'animation-observations.json', sha256: sha(observationBytes), counts: receipt.animationEvidence.counts,
            caseResults: report.caseResults.map(item => ({ caseId: item.caseId, result: item.result })) } };
    return { summary, report: suite };
}
