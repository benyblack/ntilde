// Parent-side report builder for the erase-character / saved-lines qualification. Re-derives every conclusion from the retained
// parent journal and the independent contract grader, refuses any tampering, and requires the pinned probes' own grading to agree:
// each pinned result is a pure function of the observed states of its own fixture. Both pinned probes decide, so the contract may
// never be more permissive than the pinned probe; any difference in verdict semantics is recorded with both verdicts.
import { eraseCharsHistoryCaseIds, eraseCharsHistoryContractsVersion, eraseCharsHistoryNoncePrefix, eraseCharsHistorySuiteId,
    makeEraseCharsHistoryContracts, gradeEraseCharsHistoryCase, eraseCharsHistoryCellAt, eraseCharsHistoryObservation,
    validateEraseCharsHistoryScreen, pinnedEraseCharsHistoryNotes } from './erase-chars-history-contracts.mjs';
import { validateColorEvidence, buildColorReport, incomplete, decided } from './sgr-color-grading-core.mjs';
import { eraseCharsHistoryAdapterId } from './erase-chars-history-grading-collector.mjs';
import { eraseCharsHistoryRecordingContext } from './erase-chars-history-collector.mjs';
import { screenSnapshot } from './agent-observer.mjs';

/** The pinned rowCells projection: the stored character of the first five cells, or null when it is not a string. */
const rowCells = screen => Array.from({ length: 5 }, (_, col) => {
    const char = eraseCharsHistoryCellAt(screen, 0, col).char;
    return typeof char === 'string' ? char : null;
});
/** The pinned isBlank. */
const isBlank = char => char === '' || char === ' ';
/** The pinned getScrollback shape of one screen: its rows plus its retained history lines. */
const scrollbackOf = screen => ({ screenLines: screen.rows, totalLines: screen.rows + screen.bufferState.scrollbackLines, viewportY: 0 });
const pinnedCells = Object.freeze(['blank', 'blank', 'blank', 'D', 'E']);

/** Re-derive the pinned eraseRowResult over the initial, seed and erased screens of the pinned ECH fixture, mirroring the pinned result shapes exactly. */
function replayCharacter(contract, screens) {
    const [initial, seed, erased] = [0, 1, 2].map(fed => screens[contract.pinnedReadAt[fed]]);
    const rows = scrollbackOf(initial).screenLines;
    if (!Number.isSafeInteger(initial.cols) || initial.cols < 6 || !Number.isSafeInteger(rows) || rows < 1) {
        return incomplete(JSON.stringify({ cols: initial.cols, rows }), pinnedEraseCharsHistoryNotes.geometry);
    }
    const before = rowCells(seed);
    const cursorBefore = { x: seed.cursorCol, y: seed.cursorRow };
    const after = rowCells(erased);
    const response = JSON.stringify({ before, cursorBefore: { x: cursorBefore.x, y: cursorBefore.y }, after });
    const fixtureReady = before.join('') === 'ABCDE' && cursorBefore.x === 0 && cursorBefore.y === 0;
    if (!fixtureReady || after.includes(null)) {
        return { pass: false, response, note: pinnedEraseCharsHistoryNotes.fixture,
            observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state' } };
    }
    return decided(pinnedCells.every((char, col) => (char === 'blank' ? isBlank(after[col] ?? '\x00') : after[col] === char)), contract, response);
}

/** Re-derive the pinned ED 3 result over the initial, before and after screens of the pinned fixture, mirroring the pinned result shapes exactly. */
function replayHistory(contract, screens) {
    const [initialScreen, beforeScreen, afterScreen] = [0, 1, 2].map(fed => screens[contract.pinnedReadAt[fed]]);
    const initial = scrollbackOf(initialScreen), rows = initial.screenLines;
    if (!Number.isSafeInteger(rows) || rows < 1 || rows > 1000) {
        return incomplete(JSON.stringify({ initial }), pinnedEraseCharsHistoryNotes.historyGeometry);
    }
    const before = scrollbackOf(beforeScreen);
    if (!Number.isSafeInteger(before.totalLines) || before.totalLines <= rows || before.screenLines !== rows) {
        return incomplete(JSON.stringify({ initial, before }), pinnedEraseCharsHistoryNotes.historyNone);
    }
    const after = scrollbackOf(afterScreen);
    const measured = Number.isSafeInteger(after.totalLines) && after.totalLines >= rows && after.screenLines === rows;
    const response = JSON.stringify({ before, after });
    if (!measured) return incomplete(response, pinnedEraseCharsHistoryNotes.historyReadback);
    return decided(after.totalLines === rows, contract, response);
}

/** Re-derive the complete pinned termless result over the pinned fixture's observed screens. */
export const replayEraseCharsHistoryPinned = (contract, screens) =>
    (contract.family === 'erase-character' ? replayCharacter(contract, screens) : replayHistory(contract, screens));

/** The geometry-dependent report spec: the contracts follow the measured geometry of the retained start frame. */
function specFor(frames) {
    const start = frames?.[0]?.screen;
    const contracts = makeEraseCharsHistoryContracts(start?.rows, start?.cols);
    return Object.freeze({ label: 'Erase-character/saved-lines', prefix: 'echh', noncePrefix: eraseCharsHistoryNoncePrefix,
        contracts, caseIds: eraseCharsHistoryCaseIds, suiteId: eraseCharsHistorySuiteId, version: eraseCharsHistoryContractsVersion,
        adapterId: eraseCharsHistoryAdapterId, derivedFrom: 'ntilde-owned-attr-rect-grading-v1',
        screenOf: value => {
            const screen = screenSnapshot(value, true, true, false, true, true, true);
            validateEraseCharsHistoryScreen(screen);
            return screen;
        },
        pinnedEvidence: ['parser-state'],
        observation: eraseCharsHistoryObservation, grade: gradeEraseCharsHistoryCase, evidence: 'owned-stored-cells-and-actual-scrollback',
        replayPinned: replayEraseCharsHistoryPinned,
        recordingContext: eraseCharsHistoryRecordingContext, replayFn: 'ntildeRunEraseCharsHistoryTermless',
        scope: 'Pinned terminfo.dev@4.0.0 termless criteria erase.character (ECH `ESC[3X` with the explicit count 3 over ABCDE at column 0) and '
            + 'erase.screen.scrollback (ED 3 `ESC[3J` after rows+2 line feeds) replayed byte-exactly on a disposable App main screen and graded from parent-journaled '
            + 'readScreen viewport text, stored cell details, the cursor, margins, modes, active buffer, actual readScrollback lines and count. Controls: the observed '
            + 'blank baseline; the exact erased and retained cells across the whole row, a wide three-row fixture, a right-edge fixture and a bold-seeded attribute fixture '
            + '(DCH shifting, whole-line erase, no-op and wrong counts are told apart; the omitted, explicit 0, 1, 5 and oversized spellings are observed, not graded); '
            + 'nonempty ordered saved lines before CSI 3J (the pinned fixture saves three blank rows, the marked control nine HIST-N rows), CSI 2J and ordinary scrolling '
            + 'keeping every saved line, CSI 3J on empty history, and scrolling after CSI 3J saving only the newly scrolled rows. History counts are physical rows. '
            + 'ASCII fixtures only; no wide-cell or combining-character erasure, wrapped-line, selective-erase, rectangle, pixel or terminal-query claim; the pinned '
            + 'pixel/query paths never carry a verdict and are not qualified; no stock grades.' });
}

/** Validate journal, run identity, binding stability and restoration. */
export const validateEraseCharsHistoryEvidence = (frames, run, runId, adapter) => validateColorEvidence(specFor(frames), frames, run, runId, adapter);

/** Re-derive and cross-check every case; throw on any disagreement. */
export const buildEraseCharsHistoryGradingReport = (frames, run, runId, adapter, options) => buildColorReport(specFor(frames), frames, run, runId, adapter, options);
