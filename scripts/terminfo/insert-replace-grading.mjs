import { screenSnapshot } from './agent-observer.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { sgrAttrsCellAt } from './sgr-attrs-contracts.mjs';
import { crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement, validateGradedCaptureRecord, validateGradedJournal } from './graded-plan.mjs';
import { gradeInsertReplaceEffect, insertReplaceContract, insertReplaceContractsVersion, insertReplaceNoncePrefix,
    insertReplaceSuiteId, insertReplaceTerminalWrites } from './insert-replace-contracts.mjs';
import { insertReplaceAdapterId } from './insert-replace-grading-collector.mjs';
import { lrMarginsAdapterId } from './lr-margins-grading-collector.mjs';
import { insertReplaceTermlessContext, insertReplaceTerminalContext } from './insert-replace-collector.mjs';
import { replayInsertReplacePinned, replayInsertReplaceTerminal } from './insert-replace-pinned-replay.mjs';

const screenOf = value => screenSnapshot(value, true, true, false, true, true, true, false, false, true);
const roleScreen = (screens, role) => screens[insertReplaceContract.roles.indexOf(role)];
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

/** Rederive both pinned paths and the separate effect from the journal and run record. */
export function buildInsertReplaceReport(frames, run, runId, adapter, { replayModule = null, contractsVersion = insertReplaceContractsVersion } = {}) {
    if (contractsVersion !== 1) throw new Error('Unknown IRM contracts version.');
    validateGradedJournal(frames, { frameCount: insertReplaceContract.roles.length + 3, prefix: 'irm', noncePrefix: insertReplaceNoncePrefix,
        runId, label: 'insert/replace', screenOf });
    if (!adapter || adapter.id !== insertReplaceAdapterId || adapter.derivedFromAdapterId !== lrMarginsAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || run?.schemaVersion !== 1 || run.adapterId !== adapter.id || !same(run.probeIds, ['modes.insert-replace'])) throw new Error('Invalid IRM adapter or run identity.');
    const initial = frames[0].screen, final = frames.at(-1).screen;
    if (!same(run.original, { insertMode: initial.bufferState.insertMode, autoWrap: initial.bufferState.autoWrap })
        || !same(initial.bufferState, final.bufferState) || initial.bufferState.alternateScreen !== false) throw new Error('IRM original state was not restored.');
    const binding = colorBindingHash(initial.colorBinding);
    if (frames.some(frame => colorBindingHash(frame.screen.colorBinding) !== binding)) throw new Error('IRM color binding changed.');
    validateGradedCaptureRecord(insertReplaceContract, run, 'insert/replace');
    const readFrames = frames.slice(2, -1);
    crossCheckGradedCapture(insertReplaceContract, run, readFrames, 'insert/replace');
    const screens = readFrames.map(frame => frame.screen);
    const seedScreen = roleScreen(screens, 'seed'), home = roleScreen(screens, 'home'), inserted = roleScreen(screens, 'inserted');
    const seed = Array.from({ length: 4 }, (_, col) => sgrAttrsCellAt(seedScreen, 0, col).char);
    const after = Array.from({ length: 4 }, (_, col) => sgrAttrsCellAt(inserted, 0, col).char);
    const origin = { x: home.cursorCol, y: home.cursorRow, visible: home.cursorVisible };
    const expectedPinned = replayInsertReplacePinned({ cols: seedScreen.cols, rows: seedScreen.rows, seed, origin, after });
    if (run.pinned?.probeId !== 'modes.insert-replace') throw new Error('Missing pinned IRM result.');
    requirePinnedAgreement(insertReplaceContract, run.pinned, expectedPinned, 'insert/replace');
    const terminal = roleScreen(screens, 'terminal');
    const terminalExpected = replayInsertReplaceTerminal({ cols: terminal.cols, rows: terminal.rows,
        position: { row: terminal.cursorRow + 1, col: terminal.cursorCol + 1 } });
    if (run.terminalCursorEvidence !== 'parent-observed cursor projection; not a PTY cursor reply'
        || run.terminalPinned?.probeId !== 'modes.insert-replace'
        || !same(Object.fromEntries(Object.entries(run.terminalPinned).filter(([key]) => key !== 'probeId')), terminalExpected)) {
        throw new Error('Pinned IRM terminal result differs from the projected observation.');
    }
    if (replayModule) {
        requireReplayAgreement(insertReplaceContract, replayModule.ntildeRunInsertReplaceTermless('modes.insert-replace', insertReplaceTermlessContext(screens)), run.pinned);
    }
    const shown = (role, row, from, count) => {
        const screen = roleScreen(screens, role);
        return { text: Array.from({ length: count }, (_, index) => sgrAttrsCellAt(screen, row, from + index).char).join(''),
            cursor: [screen.cursorRow, screen.cursorCol] };
    };
    const attrBefore = roleScreen(screens, 'attrBefore'), attrAfter = roleScreen(screens, 'attrInserted'), pen = roleScreen(screens, 'pen');
    const observed = seedScreen.rows < 3 || seedScreen.cols < 12 ? { geometry: { rows: seedScreen.rows, cols: seedScreen.cols } } :
        { geometry: { rows: seedScreen.rows, cols: seedScreen.cols }, wrap: 'disabled',
        seed: shown('seed', 0, 0, 8), inserted: shown('inserted', 0, 0, 8), replaced: shown('replaced', 0, 0, 8),
        wideBefore: shown('wideBefore', 1, 0, 12), wideInserted: shown('wideInserted', 1, 0, 12), wideReplaced: shown('wideReplaced', 1, 0, 12),
        edgeBefore: shown('edgeBefore', 0, seedScreen.cols - 3, 3), edgeInserted: shown('edgeInserted', 0, seedScreen.cols - 3, 3),
        attributes: { beforeD: sgrAttrsCellAt(attrBefore, 0, 3), afterD: sgrAttrsCellAt(attrAfter, 0, 5),
            beforeE: sgrAttrsCellAt(attrBefore, 0, 4), afterE: sgrAttrsCellAt(attrAfter, 0, 6),
            writtenX: sgrAttrsCellAt(attrAfter, 0, 3), writtenY: sgrAttrsCellAt(attrAfter, 0, 4), penX: sgrAttrsCellAt(pen, 0, 0), penY: sgrAttrsCellAt(pen, 0, 1) } };
    return { schemaVersion: 1, suiteId: insertReplaceSuiteId, contractsVersion, runId, adapter,
        pinned: run.pinned, terminalPinned: run.terminalPinned, terminalCursorEvidence: run.terminalCursorEvidence,
        terminalWrites: insertReplaceTerminalWrites, effect: gradeInsertReplaceEffect(screens), observed,
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, snapshots: frames };
}

/** Re-execute the terminal path when the retained adapter module is available. */
export async function verifyInsertReplaceTerminalReplay(module, screen, recorded) {
    const projected = await module.ntildeRunInsertReplaceTerminal('modes.insert-replace', insertReplaceTerminalContext(screen));
    if (!same(projected, recorded)) throw new Error('Pinned IRM terminal replay differs.');
}
