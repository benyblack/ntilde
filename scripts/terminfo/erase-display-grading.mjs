// Parent-side report builder for the erase-in-display qualification. Re-derives every conclusion from the
// retained parent journal and the independent contract grader, refuses any tampering, and requires the
// pinned probe's own eraseScreenResult grading to agree: the pinned result is a pure function of the observed
// screens of its own fixture. The contract may never be more permissive than the pinned probe; any difference
// in verdict semantics is recorded with both verdicts.
import { eraseDisplayContracts, eraseDisplayCaseIds, eraseDisplayContractsVersion, eraseDisplayNoncePrefix, eraseDisplaySuiteId,
    gradeEraseDisplayCase, eraseDisplayCellAt, eraseDisplayObservation, eraseDisplayScrollback, validateEraseDisplayScreen,
    pinnedGeometryNote, pinnedFixtureNote } from './erase-display-contracts.mjs';
import { validateColorEvidence, buildColorReport, decided } from './sgr-color-grading-core.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { eraseDisplayAdapterId } from './erase-display-grading-collector.mjs';
import { eraseDisplayRecordingContext } from './erase-display-collector.mjs';

/** Erase-in-display screens carry attributes, stored details (with overline), resolved colors, buffer state and history together. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, false, true, true, true);
    validateEraseDisplayScreen(screen);
    return screen;
};

/** The suite's report spec: identities, pinned replay and scope. */
const spec = Object.freeze({ label: 'Erase-in-display', prefix: 'erdi', noncePrefix: eraseDisplayNoncePrefix,
    contracts: eraseDisplayContracts, caseIds: eraseDisplayCaseIds, suiteId: eraseDisplaySuiteId, version: eraseDisplayContractsVersion,
    adapterId: eraseDisplayAdapterId, derivedFrom: 'ntilde-owned-erase-line-grading-v1',
    screenOf,
    observation: eraseDisplayObservation, grade: gradeEraseDisplayCase,
    replayPinned: (contract, screens) => replayEraseDisplayPinned(contract, screens),
    recordingContext: eraseDisplayRecordingContext, replayFn: 'ntildeRunEraseDisplayTermless',
    scope: 'Pinned terminfo.dev@4.0.0 erase-in-display termless criteria (erase.screen.below with ED 0, erase.screen.above with ED 1 and erase.screen.all with ED 2, '
            + 'all with the explicit parameter spelling) replayed byte-exactly on a disposable App and graded from parent-journaled readScreen stored cells, the cursor, '
            + 'stored attribute details and buffer state, with controls for the unmodified seed (no-op), every other ED spelling (omitted, explicit 0, 1 and 2: wrong direction, '
            + 'accidental whole-screen clearing), a full-screen fill control for unaffected rows and columns and the cursor-cell boundary, a boundary control with the cursor on the '
            + 'first or last column, a bold-seeded attribute control (retained attributes required; background-color erase observed, not required) and a main-screen history control '
            + '(saved lines must survive: ED 2 is not ED 3). Stored state only: the pinned terminal pixel path is capture-only upstream and is not qualified; CSI 3J is not graded; no stock grades.' });

/** Validate journal, run identity, binding stability and restoration. */
export const validateEraseDisplayEvidence = (frames, run, runId, adapter) => validateColorEvidence(spec, frames, run, runId, adapter);

/** The pinned rowCells projection: the stored character of the first five cells, or null when it is not a string. */
const rowCells = (screen, row) => Array.from({ length: 5 }, (_, col) => {
    const char = eraseDisplayCellAt(screen, row, col).char;
    return typeof char === 'string' ? char : null;
});
/** The pinned isBlank. */
const isBlank = char => char === '' || char === ' ';

/** Re-derive the complete pinned eraseScreenResult over the initial, setup and erased screens of the pinned fixture,
 * mirroring the pinned result shapes exactly. */
export function replayEraseDisplayPinned(contract, screens) {
    const initial = screens[contract.initialRead], setup = screens[contract.setupRead], erased = screens[contract.pinnedRead];
    const initialScrollback = eraseDisplayScrollback(initial);
    const lines = initialScrollback.screenLines;
    if (!Number.isSafeInteger(lines) || lines < 3 || !Number.isSafeInteger(setup.cols) || setup.cols < 6) {
        // The pinned geometry refusal carries its note only inside the observation, and no assertions.
        return { pass: false, response: JSON.stringify({ initialScrollback }),
            observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note: pinnedGeometryNote } };
    }
    const middle = Math.floor(lines / 2);
    const rows = [0, middle, lines - 1];
    const before = rows.map(row => rowCells(setup, row));
    const cursorBefore = { x: setup.cursorCol, y: setup.cursorRow };
    const scrollbackBefore = eraseDisplayScrollback(setup);
    const after = rows.map(row => rowCells(erased, row));
    const cursorAfter = { x: erased.cursorCol, y: erased.cursorRow };
    const scrollbackAfter = eraseDisplayScrollback(erased);
    const response = JSON.stringify({ rows, before, cursorBefore: { x: cursorBefore.x, y: cursorBefore.y }, scrollbackBefore,
        after, cursorAfter: { x: cursorAfter.x, y: cursorAfter.y }, scrollbackAfter });
    const fixtureReady = before.every((row, index) => row.join('') === ['AAAAA', 'BBBBB', 'CCCCC'][index])
        && cursorBefore.x === 2 && cursorBefore.y === middle && Number.isInteger(scrollbackBefore.totalLines) && scrollbackBefore.totalLines >= lines
        && !after.some(row => row.some(char => char === null));
    if (!fixtureReady) {
        return { pass: false, response,
            observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note: pinnedFixtureNote } };
    }
    const cellsMatch = contract.pinnedCells.every((expectedRow, row) => [...expectedRow].every((expectedChar, col) => {
        const actual = after[row]?.[col];
        return expectedChar === ' ' ? isBlank(actual ?? '\x00') : actual === expectedChar;
    }));
    const preservedCursor = cursorAfter.x === cursorBefore.x && cursorAfter.y === cursorBefore.y;
    return decided(cellsMatch && preservedCursor, contract, response);
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export const buildEraseDisplayGradingReport = (frames, run, runId, adapter, options) => buildColorReport(spec, frames, run, runId, adapter, options);
