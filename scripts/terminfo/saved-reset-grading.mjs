// Parent-side report builder for the saved-state / reset qualification. Re-derives every conclusion from the retained
// parent journal and the independent contract grader, refuses any tampering, and requires the pinned probe's own
// grading to agree: the pinned result is a pure function of the observed screens of its own fixture. The effect
// contract is only ever stricter than the pinned probe, EXCEPT for the two cases whose pinned verdict is inconclusive
// by design (modes.altscreen-47 and modes.altscreen-1047): there the pinned verdict is preserved as the case conclusion
// and the effect contract is a separately identified verdict recorded beside it, never attributed to the pinned probe.
import { savedResetContracts, savedResetCaseIds, savedResetContractsVersion, savedResetNoncePrefix, savedResetSuiteId, savedResetEffectContractId,
    gradeSavedResetCase, projectSavedResetCells, savedResetObservation, savedResetObserved, validateSavedResetScreen,
    pinnedSeedNote, pinnedModeNote, pinnedStyledNote } from './saved-reset-contracts.mjs';
import { validateColorEvidence, reconcile as reconcileStrict, incomplete, decided } from './sgr-color-grading-core.mjs';
import { validateGradedCaptureRecord, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement } from './graded-plan.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { savedResetAdapterId } from './saved-reset-grading-collector.mjs';
import { savedResetRecordingContext } from './saved-reset-collector.mjs';

/** Journal frames carry the observed buffer state and history as well as attributes, stored details and resolved colors. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, false, true, true, true);
    validateSavedResetScreen(screen);
    return screen;
};

/** The suite's report spec: identities, pinned replay and scope. */
const spec = Object.freeze({ label: 'Saved-state/reset', prefix: 'svrs', noncePrefix: savedResetNoncePrefix,
    contracts: savedResetContracts, caseIds: savedResetCaseIds, suiteId: savedResetSuiteId, version: savedResetContractsVersion,
    adapterId: savedResetAdapterId, derivedFrom: 'ntilde-owned-scrollback-grading-v1', screenOf,
    scope: 'Pinned terminfo.dev@4.0.0 termless criteria for modes.alt-screen.exit (the seeded ?1049 roundtrip), modes.altscreen-47, modes.altscreen-1047, '
            + 'reset.sgr (ESC[1;3;7m X ESC[0m Y, a case separate from sgr.reset) and reset.decaln (ESC # 8, cell 0,0) replayed byte-exactly on a disposable App '
            + 'main screen and graded from parent-journaled readScreen stored cells, cursor, active-buffer flag, margins, mode flags and history. '
            + 'modes.altscreen-47 and modes.altscreen-1047 are inconclusive by design in the pinned probe; that verdict is preserved and the visible-buffer effect '
            + 'contracts (' + savedResetEffectContractId + ') are graded separately with controls for no-op switches, shared bank contents, accidental main clearing, '
            + 'stale saved cursors and incomplete restoration. Active-screen reads only: the inactive bank is never inspected directly. reset.method, RIS, state '
            + 'stacks, pixels and the pinned DECALN pixel path are not claimed; no stock grades.' });

/** Validate journal, run identity, binding stability and restoration. */
export const validateSavedResetEvidence = (frames, run, runId, adapter) => validateColorEvidence(spec, frames, run, runId, adapter);

/** The pinned isBlank. */
const isBlank = char => char === '' || char === ' ';
/** The pinned readAlt1049Span: the stored characters of one row prefix. */
const span = (cellAt, screen, row, length) => Array.from({ length }, (_, index) => cellAt(screen)(row, index).char).join('');
/** The pinned getCursor shape of one screen. */
const cursorOf = screen => ({ x: screen.cursorCol, y: screen.cursorRow, visible: screen.cursorVisible });

/** Re-derive the complete pinned result over the observed screens of the pinned fixture, mirroring the pinned result shapes exactly. */
export function replaySavedResetPinned(contract, screens) {
    const projectors = new Map();
    const cellAt = screen => {
        if (!projectors.has(screen)) projectors.set(screen, projectSavedResetCells(screen));
        return projectors.get(screen);
    };
    const read = fed => screens[contract.pinnedReads[fed]];
    if (contract.caseId === 'modes.alt-screen.exit') return replayExit(contract, screens, read, cellAt);
    if (contract.pinnedAlwaysInconclusive) {
        const entered = read(1).bufferState.alternateScreen === true, exited = read(2).bufferState.alternateScreen === false;
        return incomplete(JSON.stringify({ entered, exited }), pinnedModeNote);
    }
    if (contract.caseId === 'reset.sgr') {
        const before = cellAt(read(1))(0, 0), after = cellAt(read(1))(0, 1);
        const state = JSON.stringify({ before, after });
        if (before.char !== 'X' || after.char !== 'Y' || !before.bold || !before.italic || !before.inverse) return incomplete(state, pinnedStyledNote);
        return decided(!after.bold && !after.italic && !after.inverse, contract, state);
    }
    const cell = cellAt(read(1))(0, 0);
    return decided(cell.char === 'E', contract, JSON.stringify({ cell }));
}

/** The pinned altScreen1049Roundtrip(ctx, "exit") over the journaled screens. */
function replayExit(contract, screens, read, cellAt) {
    const rows = screens[0].rows, cols = screens[0].cols, needed = 9;
    if (!Number.isSafeInteger(rows) || !Number.isSafeInteger(cols) || rows < 5 || cols < needed) {
        return incomplete(JSON.stringify({ rows, cols, needed }), `1049 fixture needs at least 5 visible rows and ${needed} columns`);
    }
    const before = cursorOf(read(3));
    const seedA = span(cellAt, read(3), 0, 9), seedB = span(cellAt, read(3), 2, 9);
    if (seedA !== 'PRIMARY-A' || seedB !== 'PRIMARY-B') return incomplete(JSON.stringify({ rows, cols, seedA, seedB }), pinnedSeedNote);
    if (before.x !== 6 || before.y !== 4) {
        return incomplete(JSON.stringify({ before, expectedCursor: { x: 6, y: 4 } }), 'Parked pre-cursor was not measured at row 4/col 6');
    }
    const blank = (screen, row) => Array.from({ length: 9 }, (_, index) => isBlank(cellAt(screen)(row, index).char)).every(Boolean);
    const blankA = blank(read(4), 0), blankB = blank(read(4), 2);
    if (!blankA || !blankB) {
        return incomplete(JSON.stringify({ seedA, seedB, blankA, blankB }), 'Entry did not clear the seeded primary spans; exit cannot be attributed');
    }
    const mark = span(cellAt, read(5), 0, 8);
    if (mark !== 'ALT-MARK') return incomplete(JSON.stringify({ mark }), 'Alternate marker was not measured');
    const restoredA = span(cellAt, read(6), 0, 9), restoredB = span(cellAt, read(6), 2, 9);
    const leaked = span(cellAt, read(6), 0, 8) === 'ALT-MARK';
    const after = cursorOf(read(6));
    const state = JSON.stringify({ before, seedA, seedB, mark, restoredA, restoredB, leaked, after });
    const failures = [];
    if (restoredA !== 'PRIMARY-A') failures.push('PRIMARY-A not restored');
    if (restoredB !== 'PRIMARY-B') failures.push('PRIMARY-B not restored');
    if (leaked) failures.push('ALT-MARK leaked into the restored position');
    if (after.x !== before.x || after.y !== before.y) failures.push('saved cursor not restored');
    if (failures.length === 0) return decided(true, contract, state);
    return incomplete(state, '1049 roundtrip failed: ' + failures.join('; ')
        + '; cannot attribute failure to enter or exit: blank spans and ALT-MARK do not distinguish an alternate buffer from erasure, and cursor mismatch does not distinguish save from restore');
}

/** Compare the pinned and effect verdicts. Strict cases follow the shared rule (the contract is the finer grader and never more
 * permissive). A case whose pinned verdict is inconclusive by design keeps that verdict; a decided effect verdict is a recorded,
 * separately identified difference, and a pinned verdict other than inconclusive there is itself an error. */
export function reconcileSavedReset(contract, pinnedOutcome, effect) {
    if (!contract.pinnedAlwaysInconclusive) return reconcileStrict(contract, pinnedOutcome, effect);
    if (pinnedOutcome !== 'inconclusive') throw new Error(`Pinned ${contract.caseId} must be inconclusive by design.`);
    if (effect.outcome === 'inconclusive') return null;
    return { pinnedOutcome, contractOutcome: effect.outcome, kind: 'pinned-inconclusive-by-design' };
}

/** The conclusion's note: the pinned note when the pinned verdict is preserved, else the effect note when there is one. */
function conclusionNote(preserved, pinned, effect) {
    if (preserved) return { note: pinned.note ?? null };
    return effect.note ? { note: effect.note } : {};
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildSavedResetGradingReport(frames, run, runId, adapter, { contractsVersion = spec.version, replayModule = null } = {}) {
    validateSavedResetEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const caseResults = [];
    for (const [position, contract] of spec.contracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${spec.label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, spec.label);
        if (!Number.isSafeInteger(recorded.pinnedFed) || recorded.pinnedFed < 0 || recorded.pinnedFed > contract.pinnedFeeds.length) {
            throw new Error(`Invalid ${contract.caseId} pinned feed count.`);
        }
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, spec.label);
        const screens = readFrames.map(frame => frame.screen);
        const observation = savedResetObservation(contract, screens);
        const effect = gradeSavedResetCase(contract, observation, contractsVersion);
        const pinned = recorded.pinned;
        if (pinned?.probeId !== contract.caseId || pinned.observation?.evidence !== 'parser-state') {
            throw new Error(`Missing pinned ${contract.caseId} grading result.`);
        }
        requirePinnedAgreement(contract, pinned, replaySavedResetPinned(contract, screens), spec.label);
        const divergence = reconcileSavedReset(contract, pinned.observation.outcome, effect);
        if (replayModule) {
            const context = savedResetRecordingContext(contract, screens);
            const replay = replayModule.ntildeRunSavedResetTermless(contract.caseId, context);
            requireReplayAgreement(contract, replay, pinned);
            if (context.fedCount !== recorded.pinnedFed) throw new Error(`Pinned ${contract.caseId} replay fed a different number of pinned writes.`);
        }
        const theme = screens[0].colorBinding;
        const preserved = contract.pinnedAlwaysInconclusive;
        caseResults.push({ caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion, pinnedFeeds: contract.pinnedFeeds,
            pinnedFed: recorded.pinnedFed, terminalFixture: contract.terminalFixture, feeds: recorded.feeds, barriers: recorded.barriers,
            reads: recorded.reads.map(read => read.cursor), observed: savedResetObserved(contract, observation),
            colorBinding: { reverseVideo: theme.reverseVideo, themeName: theme.themeName, foregroundArgb: theme.foregroundArgb, backgroundArgb: theme.backgroundArgb,
                minimumContrast: theme.minimumContrast, paletteOverrideIndices: [...theme.paletteOverrideIndices], bindingSha256: colorBindingHash(theme) },
            pinned: { pass: pinned.pass, response: pinned.response, note: pinned.note ?? null, observation: pinned.observation, assertions: pinned.assertions ?? null },
            effectContract: { id: contract.effectContract, version: contractsVersion, outcome: effect.outcome,
                ...(effect.layers ? { layers: effect.layers } : {}), ...(effect.note ? { note: effect.note } : {}),
                attribution: preserved ? 'separately identified Ntilde effect contract; not the pinned probe' : 'finer grader over the pinned fixture' },
            contractResult: effect, ...(divergence ? { divergence } : {}),
            conclusion: { outcome: preserved ? pinned.observation.outcome : effect.outcome, evidence: 'owned-stored-and-resolved-cells',
                grading: preserved ? 'pinned-termless-preserved-by-design' : 'pinned-termless-plus-contract-v' + contractsVersion,
                ...(!preserved && effect.layers ? { layers: effect.layers } : {}),
                ...conclusionNote(preserved, pinned, effect),
                scope: contract.appearanceScope } });
    }
    return { schemaVersion: 1, suiteId: spec.suiteId, runId, contractsVersion, adapter, scope: spec.scope, initialSnapshot: 0,
        restoredSnapshot: frames.length - 1, cases: caseResults, snapshots: frames };
}
