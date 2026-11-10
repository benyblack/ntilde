// Parent-side report builder for the column-editing qualification. Re-derives every conclusion from the
// retained parent journal and the independent contract grader, refuses any tampering, and requires the
// pinned probe's own grading to agree: the pinned result is a pure function of the observed screens of its
// own fixture. The contract may never be more permissive than the pinned probe; any difference in verdict
// semantics is recorded with both verdicts.
import { colEditContracts, colEditCaseIds, colEditContractsVersion, colEditNoncePrefix, colEditSuiteId,
    gradeColEditCase, colEditCellAt, colEditObservation, validateColEditScreen, pinnedNotes } from './col-edit-contracts.mjs';
import { buildColorReport, validateColorEvidence } from './sgr-color-grading-core.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { colEditAdapterId } from './col-edit-grading-collector.mjs';
import { colEditRecordingContext } from './col-edit-collector.mjs';

/** Column-edit screens carry attributes, stored details (with overline and protection), resolved colors and the App's stored buffer state. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, false, true, true, true, false, true);
    validateColEditScreen(screen);
    return screen;
};

/** The suite's report spec: identities, pinned replay and scope. */
const spec = Object.freeze({ label: 'Column-editing', prefix: 'cole', noncePrefix: colEditNoncePrefix,
    contracts: colEditContracts, caseIds: colEditCaseIds, suiteId: colEditSuiteId, version: colEditContractsVersion,
    adapterId: colEditAdapterId, derivedFrom: 'ntilde-owned-rectangle-editing-grading-v1',
    screenOf,
    observation: colEditObservation, grade: gradeColEditCase,
    replayPinned: (contract, screens) => replayColEditPinned(contract, screens),
    recordingContext: colEditRecordingContext, replayFn: 'ntildeRunColEditTermless',
    scope: 'Pinned terminfo.dev@4.0.0 column-editing termless criteria (editing.sl with ESC[2 @, editing.sr with ESC[2 A, editing.decic with ESC[2\'} and editing.decdc with '
            + 'ESC[2\'~, each over its pinned seed and setup cursor) replayed byte-exactly on a disposable App and graded from parent-journaled readScreen stored cells of the whole screen, '
            + 'the cursor, the App\'s stored margins, origin mode and insert mode, stored attribute tuples and per-cell DECSCA protection, with controls over a seed at both the left and the '
            + 'right edge for one, omitted, zero, three and oversized (clamped) counts, scroll margins with the cursor inside, above and below them, origin mode, insert mode, '
            + 'non-admitted identities (extra parameter, subparameter, private leader), the start column (SL/SR from the left margin whatever the cursor column; DECIC/DECDC at the '
            + 'first and the last column), moved attributed cells under a different pen with attribute-free introduced blanks, and moved protection. The pending-wrap state after an edit '
            + 'at the last column is observed, never graded. Stored ASCII state only with full-width horizontal margins: left/right margins (DECLRMM) are not implemented or claimed; the '
            + 'pinned terminal pixel path never carries a verdict and is not qualified; no Unicode, wide-cell, scaled-text, image-plane, DECRQCRA, rectangle or rendered-appearance claim; no stock grades.' });

/** Validate journal, run identity, binding stability and restoration. */
export const validateColEditEvidence = (frames, run, runId, adapter) => validateColorEvidence(spec, frames, run, runId, adapter);

/** The pinned isBlank. */
const isBlank = char => char === '' || char === ' ';
/** The pinned cell projection: the stored character of every cell of the first `rows` rows and `cols` columns, as arrays of characters. */
const charsOf = (screen, rows, cols) => Array.from({ length: rows }, (_, row) => Array.from({ length: cols }, (_, col) => colEditCellAt(screen, row, col).char));

/** The pinned observation outcome of a tri-state pass. */
function outcomeOf(pass) {
    if (pass === null) return 'inconclusive';
    return pass ? 'supported' : 'unsupported';
}

/** The pinned parserStateResult shape, key order included. */
function parserState(pass, expected, state, note) {
    const response = JSON.stringify(state);
    return { pass: pass === true, response, ...note && { note },
        observation: { outcome: outcomeOf(pass),
            ...pass === null && { reason: 'insufficient-evidence' }, evidence: 'parser-state', ...note && { note } },
        ...pass !== null && { assertions: [{ kind: pass ? 'positive' : 'negative', expected, observed: response }] } };
}

/** The pinned geometry guard and seed widths of each mode: rows, minimum columns, the seed rows (clipped like the probe) and the after width. */
const shapes = Object.freeze({
    left: { rows: 2, cols: 9, seeds: cols => ['ABCDEFGHIJK', 'JKLMNOPQRST'].map(line => line.slice(0, Math.min(cols, 11))), afterWidth: 9, setup: [0, 0] },
    right: { rows: 2, cols: 9, seeds: () => ['ABCDEFGHI', 'JKLMNOPQR'], afterWidth: 9, setup: [0, 0] },
    insert: { rows: 3, cols: 8, seeds: () => ['ABCDEFGH', 'IJKLMNOP', 'QRSTUVWX'], afterWidth: 8, setup: [1, 2] },
    delete: { rows: 3, cols: 8, seeds: cols => ['ABCDEFGHIJ', 'IJKLMNOPQR', 'QRSTUVWXab'].map(line => line.slice(0, Math.min(cols, 10))), afterWidth: 8, setup: [1, 2] },
});

/** The pinned per-cell verdict of each mode over the before cells (`cols` is the measured page width). */
const cellRules = Object.freeze({
    left: (char, row, col, before, cols) => (col + 2 < cols ? char === before[row]?.[col + 2] : isBlank(char)),
    right: (char, row, col, before) => (col < 2 ? isBlank(char) : char === before[row]?.[col - 2]),
    insert: (char, row, col, before) => {
        if (col < 2) return true;
        return col < 4 ? isBlank(char) : char === before[row]?.[col - 2];
    },
    delete: (char, row, col, before, cols) => {
        if (col < 2) return true;
        return col + 2 < cols ? char === before[row]?.[col + 2] : isBlank(char);
    },
});

/** Re-derive the complete pinned SL/SR/DECIC/DECDC result over the before, setup and edited screens of the pinned fixture,
 * mirroring the pinned result shapes exactly (every mode reads arrays of single characters). */
export function replayColEditPinned(contract, screens) {
    const before = screens[contract.beforeRead], setup = screens[contract.setupRead], edited = screens[contract.pinnedRead];
    const mode = contract.mode;
    const notes = pinnedNotes[mode];
    const shape = shapes[mode];
    const cols = setup.cols, rows = setup.rows;
    if (!Number.isSafeInteger(cols) || cols < shape.cols || !Number.isSafeInteger(rows) || rows < shape.rows) {
        return parserState(null, notes.expected, { cols, rows }, notes.geometry);
    }
    const seeds = shape.seeds(cols);
    const beforeCells = charsOf(before, shape.rows, seeds[0].length);
    if (!beforeCells.every((line, row) => line.every((char, col) => char === seeds[row]?.[col]))) {
        return parserState(null, notes.expected, { before: beforeCells }, notes.seed);
    }
    const cursor = { x: setup.cursorCol, y: setup.cursorRow, visible: setup.cursorVisible };
    if (cursor.x !== shape.setup[1] || cursor.y !== shape.setup[0]) return parserState(null, notes.expected, { before: beforeCells, setup: cursor }, notes.cursor);
    const after = charsOf(edited, shape.rows, shape.afterWidth);
    const rule = cellRules[mode];
    const verdict = () => after.every((line, row) => line.every((char, col) => rule(char, row, col, beforeCells, cols)));
    if (notes.controls === null) return parserState(verdict(), notes.expected, { before: beforeCells, setup: cursor, after });
    const controlsValid = after.every((line, row) => line.slice(0, 2).every((char, col) => char === beforeCells[row]?.[col]));
    return parserState(controlsValid ? verdict() : null, notes.expected, { before: beforeCells, setup: cursor, after }, controlsValid ? undefined : notes.controls);
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export const buildColEditGradingReport = (frames, run, runId, adapter, options) => buildColorReport(spec, frames, run, runId, adapter, options);
