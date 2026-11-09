// Parent-side report builder for the line-editing qualification. Re-derives every conclusion from the
// retained parent journal and the independent contract grader, refuses any tampering, and requires the
// pinned probe's own grading to agree: the pinned result is a pure function of the observed screens of its
// own fixture. The contract may never be more permissive than the pinned probe; any difference in verdict
// semantics is recorded with both verdicts.
import { lineEditContracts, lineEditCaseIds, lineEditContractsVersion, lineEditNoncePrefix, lineEditSuiteId,
    gradeLineEditCase, lineEditCellAt, lineEditObservation, validateLineEditScreen, pinnedNotes } from './line-edit-contracts.mjs';
import { buildColorReport, validateColorEvidence } from './sgr-color-grading-core.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { lineEditAdapterId } from './line-edit-grading-collector.mjs';
import { lineEditRecordingContext } from './line-edit-collector.mjs';

/** Line-edit screens carry attributes, stored details (with overline), resolved colors and the App's stored buffer state. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, false, true, true, true);
    validateLineEditScreen(screen);
    return screen;
};

/** The suite's report spec: identities, pinned replay and scope. */
const spec = Object.freeze({ label: 'Line-editing', prefix: 'ledt', noncePrefix: lineEditNoncePrefix,
    contracts: lineEditContracts, caseIds: lineEditCaseIds, suiteId: lineEditSuiteId, version: lineEditContractsVersion,
    adapterId: lineEditAdapterId, derivedFrom: 'ntilde-owned-char-edit-grading-v1',
    screenOf,
    observation: lineEditObservation, grade: gradeLineEditCase,
    replayPinned: (contract, screens) => replayLineEditPinned(contract, screens),
    recordingContext: lineEditRecordingContext, replayFn: 'ntildeRunLineEditTermless',
    scope: 'Pinned terminfo.dev@4.0.0 line-editing termless criteria (editing.insert-lines with ESC[1L and editing.delete-lines with ESC[1M, each at row 2 column 1 '
            + 'over four seeded rows) replayed byte-exactly on a disposable App and graded from parent-journaled readScreen stored cells of the whole screen, the cursor, '
            + 'the App\'s stored margins, origin mode and insert mode, and stored attribute details, with controls for the unmodified seed (no-op), the omitted, zero, two and '
            + 'oversized counts (observed), a full-height boundary at the second-to-last row, a DECSTBM scroll region with the cursor inside, above and below it, origin mode inside the '
            + 'region, and an attribute control (moved cells keep their stored tuple; new blank lines carry no attribute but a background; background fill observed, not required). '
            + 'Stored ASCII state only: the pinned terminal pixel path never carries a verdict and is not qualified; scrollback is observed on the alternate screen only; '
            + 'no Unicode, wide-cell or history qualification; no stock grades.' });

/** Validate journal, run identity, binding stability and restoration. */
export const validateLineEditEvidence = (frames, run, runId, adapter) => validateColorEvidence(spec, frames, run, runId, adapter);

/** The pinned isBlank. */
const isBlank = char => char === '' || char === ' ';
/** The pinned 5-cell row projection: the stored character of the first five cells of a row. */
const cellsOf = (screen, rows) => Array.from({ length: rows }, (_, row) => Array.from({ length: 5 }, (_, col) => lineEditCellAt(screen, row, col).char));
const textOf = cells => cells.join('');

/** The pinned parserStateResult shape, key order included. */
function parserState(pass, expected, state, note) {
    const response = JSON.stringify(state);
    return { pass: pass === true, response, ...note && { note },
        observation: { outcome: pass === null ? 'inconclusive' : pass ? 'supported' : 'unsupported',
            ...pass === null && { reason: 'insufficient-evidence' }, evidence: 'parser-state', ...note && { note } },
        ...pass !== null && { assertions: [{ kind: pass ? 'positive' : 'negative', expected, observed: response }] } };
}

/** Re-derive the complete pinned IL/DL result over the before, setup and edited screens of the pinned fixture,
 * mirroring the pinned result shapes exactly. */
export function replayLineEditPinned(contract, screens) {
    const before = screens[contract.beforeRead], setup = screens[contract.setupRead], edited = screens[contract.pinnedRead];
    const notes = pinnedNotes[contract.mode];
    const screenLines = setup.rows;
    if (!Number.isSafeInteger(setup.cols) || setup.cols < 6 || !Number.isSafeInteger(screenLines) || screenLines < 4) {
        return parserState(null, notes.expected, { cols: setup.cols, screenLines }, notes.geometry);
    }
    const seed = ['AAAAA', 'BBBBB', 'CCCCC', 'DDDDD'];
    const beforeCells = cellsOf(before, 4);
    if (beforeCells.some((cells, row) => textOf(cells) !== seed[row])) return parserState(null, notes.expected, { before: beforeCells }, notes.seed);
    const cursor = { x: setup.cursorCol, y: setup.cursorRow, visible: setup.cursorVisible };
    if (cursor.x !== 0 || cursor.y !== 1) return parserState(null, notes.expected, { before: beforeCells, setup: cursor }, notes.cursor);
    const after = cellsOf(edited, contract.pinnedRows);
    const controlsValid = textOf(after[0]) === 'AAAAA';
    const verdict = !controlsValid ? null : contract.mode === 'insert'
        ? after[1].every(isBlank) && textOf(after[2]) === 'BBBBB' && textOf(after[3]) === 'CCCCC'
        : textOf(after[1]) === 'CCCCC' && textOf(after[2]) === 'DDDDD';
    return parserState(verdict, notes.expected, { before: beforeCells, setup: cursor, after }, controlsValid ? undefined : notes.controls);
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export const buildLineEditGradingReport = (frames, run, runId, adapter, options) => buildColorReport(spec, frames, run, runId, adapter, options);
