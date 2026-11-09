// Synthetic, fully valid scrolling/history grading evidence for portable tests. Every frame, run-log entry and
// pinned result is produced through the same production validators used for real measurements. The viewport and
// history of each read come from the byte-interpreting model, so negative tests perturb those states directly;
// the hand-derived literal expectations live in the contract tests, which is the second oracle.
import { snapshotHash } from '../agent-observer.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { makeScrollbackContracts, scrollbackReset, scrollbackNoncePrefix, scrollbackObservation, gradeScrollbackCase } from '../scrollback-contracts.mjs';
import { replayScrollbackPinned } from '../scrollback-grading.mjs';
import { scrollbackAdapterId } from '../scrollback-grading-collector.mjs';
import { paletteBinding, syntheticSgrPaletteScreen } from './sgr-palette-evidence.mjs';

export { paletteBinding } from './sgr-palette-evidence.mjs';

/** Synthetic geometry: above every contract floor, small enough for fast tests. */
export const scrollbackRows = 16;
export const scrollbackCols = 60;
const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';

/** A pristine state: blank viewport, cursor home, full margins, main screen, empty history. */
export const blankState = (rows = scrollbackRows) => ({ lines: Array.from({ length: rows }, () => ''), cursor: [0, 0], top: 0, bottom: rows - 1,
    alternate: false, history: [] });

/** One complete valid observation screen of a modeled state; `details` overrides buffer-state fields to build negative evidence. */
export function syntheticScrollbackScreen(state, binding = paletteBinding(), details = {}) {
    const base = syntheticSgrPaletteScreen([], binding);
    const rows = state.lines.length, cols = scrollbackCols, total = rows * cols;
    const scale = runs => runs.map(run => ({ ...run, count: total }));
    if (base.attributeRuns.length !== 1 || base.cellDetails.length !== 1 || base.resolvedColors.length !== 1) throw new Error('Blank synthetic screen must be one run.');
    return { ...base, rows, cols, lines: [...state.lines], cursorRow: state.cursor[0], cursorCol: state.cursor[1], cursorVisible: true,
        attributeRuns: scale(base.attributeRuns), resolvedColors: scale(base.resolvedColors), cellDetails: scale(base.cellDetails),
        bufferState: { scrollTop: state.top, scrollBottom: state.bottom, originMode: false, autoWrap: true, insertMode: false, pendingWrap: false,
            alternateScreen: state.alternate, scrollbackLines: state.history.length, ...state.details, ...details },
        history: { startLine: 0, totalLines: state.history.length, lines: [...state.history] } };
}

const frame = (index, kind, screen, barrier) => ({ index, kind, paneId, barrier, screen, sha256: snapshotHash(screen) });

/** The synthetic contracts at the synthetic geometry. */
export const syntheticScrollbackContracts = () => makeScrollbackContracts(scrollbackRows, scrollbackCols);

/** The modeled states of every read of one case, in plan order; `edit(role, state)` may return a replacement state. */
export function syntheticScrollbackStates(contract, edit = (_role, state) => state) {
    return contract.roles.map(role => edit(role.role, structuredClone(role.expected)));
}

/** The observed screens of one case. */
export const syntheticScrollbackScreens = (contract, binding = paletteBinding(), edit) =>
    syntheticScrollbackStates(contract, edit).map(state => syntheticScrollbackScreen(state, binding));

/** A complete valid synthetic run: frames, worker run log and adapter facts. `edit(caseId, role, state)` builds negative evidence. */
export function syntheticScrollbackEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), edit) {
    const contracts = syntheticScrollbackContracts();
    const nonce = scrollbackNoncePrefix + runId;
    const blank = syntheticScrollbackScreen(blankState(), binding);
    const bindScreen = syntheticScrollbackScreen({ ...blankState(), lines: [nonce, ...blankState().lines.slice(1)], cursor: [0, nonce.length] }, binding);
    const frames = [frame(0, 'scrl-start', blank, null), frame(1, 'scrl-bind', bindScreen, deviceAttribute)];
    const cases = contracts.map(contract => {
        const feeds = planFeeds(contract);
        const barriers = feeds.map(() => deviceAttribute);
        const screens = syntheticScrollbackScreens(contract, binding, edit ? (role, state) => edit(contract.caseId, role, state) : undefined);
        for (const screen of screens) frames.push(frame(frames.length, 'scrl-read', screen, deviceAttribute));
        const pinned = { probeId: contract.caseId, ...replayScrollbackPinned(contract, screens) };
        return { caseId: contract.caseId, feeds, barriers,
            reads: screens.map(screen => ({ kind: 'state', cursor: [screen.cursorRow, screen.cursorCol] })), pinned };
    });
    frames.push(frame(frames.length, 'scrl-finish', blank, deviceAttribute));
    const run = { schemaVersion: 1, adapterId: scrollbackAdapterId, probeIds: contracts.map(contract => contract.caseId), cases };
    const adapter = { id: scrollbackAdapterId, derivedFromAdapterId: 'ntilde-owned-line-editing-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, reset: scrollbackReset };
}

/** The independent contract verdict for one synthetic case. */
export function syntheticScrollbackGrade(contract, binding = paletteBinding(), edit) {
    return gradeScrollbackCase(contract, scrollbackObservation(contract, syntheticScrollbackScreens(contract, binding, edit)));
}
