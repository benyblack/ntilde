// Parent-side report builder for the rectangle-editing qualification. Re-derives every conclusion from the
// retained parent journal and the independent contract grader, refuses any tampering, and requires the
// pinned probe's own grading to agree: the pinned result is a pure function of the observed screens of its
// own fixture. The contract may never be more permissive than the pinned probe; any difference in verdict
// semantics is recorded with both verdicts.
import { rectEditContracts, rectEditCaseIds, rectEditContractsVersion, rectEditNoncePrefix, rectEditSuiteId,
    gradeRectEditCase, rectEditCellAt, rectEditObservation, validateRectEditScreen, pinnedNotes } from './rect-edit-contracts.mjs';
import { buildColorReport, validateColorEvidence } from './sgr-color-grading-core.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { rectEditAdapterId } from './rect-edit-grading-collector.mjs';
import { rectEditRecordingContext } from './rect-edit-collector.mjs';

/** Rectangle-edit screens carry attributes, stored details (with overline), resolved colors and the App's stored buffer state. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, false, true, true, true);
    validateRectEditScreen(screen);
    return screen;
};

/** The suite's report spec: identities, pinned replay and scope. */
const spec = Object.freeze({ label: 'Rectangle-editing', prefix: 'rect', noncePrefix: rectEditNoncePrefix,
    contracts: rectEditContracts, caseIds: rectEditCaseIds, suiteId: rectEditSuiteId, version: rectEditContractsVersion,
    adapterId: rectEditAdapterId, derivedFrom: 'ntilde-owned-erase-attrs-region-grading-v1',
    screenOf,
    observation: rectEditObservation, grade: gradeRectEditCase,
    replayPinned: (contract, screens) => replayRectEditPinned(contract, screens),
    recordingContext: rectEditRecordingContext, replayFn: 'ntildeRunRectEditTermless',
    scope: 'Pinned terminfo.dev@4.0.0 rectangle-editing termless criteria (editing.decfra with ESC[88;1;1;3;5$x, editing.decera with ESC[1;1;3;5$z and editing.deccra with '
            + 'ESC[1;1;2;5;1;5;10$v, each over its pinned seed) replayed byte-exactly on a disposable App and graded from parent-journaled readScreen stored cells of the whole screen, '
            + 'the cursor, the App\'s stored margins, origin mode and insert mode, and stored attribute tuples, with controls for the single-cell, inclusive-border, omitted, zero and oversized '
            + '(clipped) rectangles, combined no-op groups (invalid fill codes, reversed borders, extra parameters), origin mode and scroll margins, insert mode, the live pen (filled cells '
            + 'carry the stored tuple of a reference cell printed under the same pen, including a literal-RGB pen), attributed seed cells, unconditional erasure over DECSCA-marked text, '
            + 'and for DECCRA a non-overlapping copy, overlap in every direction (a smeared incremental copy is a mismatch), a page-onto-itself copy, destination clipping and source attribute preservation. '
            + 'Unsupported DECCRA pages are an Ntilde policy that is observed, never graded. Stored ASCII state only: the pinned terminal pixel path never carries a verdict and is not qualified; '
            + 'no Unicode, wide-cell, scaled-text, image-plane, protected-cell, DECSERA, DECCARA/DECRARA, DECSACE, checksum or rendered-appearance claim; no stock grades.' });

/** Validate journal, run identity, binding stability and restoration. */
export const validateRectEditEvidence = (frames, run, runId, adapter) => validateColorEvidence(spec, frames, run, runId, adapter);

/** The pinned isBlank. */
const isBlank = char => char === '' || char === ' ';
/** The pinned cell projection: the stored character of every cell of the first `rows` rows and `cols` columns. */
const charsOf = (screen, rows, cols) => Array.from({ length: rows }, (_, row) => Array.from({ length: cols }, (_, col) => rectEditCellAt(screen, row, col).char));
/** The pinned row text projection. */
const textsOf = cells => cells.map(line => line.join(''));

/** The pinned parserStateResult shape, key order included. */
function parserState(pass, expected, state, note) {
    const response = JSON.stringify(state);
    return { pass: pass === true, response, ...note && { note },
        observation: { outcome: pass === null ? 'inconclusive' : pass ? 'supported' : 'unsupported',
            ...pass === null && { reason: 'insufficient-evidence' }, evidence: 'parser-state', ...note && { note } },
        ...pass !== null && { assertions: [{ kind: pass ? 'positive' : 'negative', expected, observed: response }] } };
}

/** Re-derive the complete pinned DECFRA/DECERA/DECCRA result over the before, setup and edited screens of the pinned fixture,
 * mirroring the pinned result shapes exactly (DECFRA and DECCRA read row strings; DECERA reads arrays of single characters). */
export function replayRectEditPinned(contract, screens) {
    const before = screens[contract.beforeRead], setup = screens[contract.setupRead], edited = screens[contract.pinnedRead];
    const notes = pinnedNotes[contract.mode];
    const screenLines = setup.rows;
    const need = contract.mode === 'copy' ? { rows: 6, cols: 14 } : { rows: 3, cols: 6 };
    if (!Number.isSafeInteger(setup.cols) || setup.cols < need.cols || !Number.isSafeInteger(screenLines) || screenLines < need.rows) {
        return parserState(null, notes.expected, { cols: setup.cols, rows: screenLines }, notes.geometry);
    }
    const cursor = { x: setup.cursorCol, y: setup.cursorRow, visible: setup.cursorVisible };
    const beforeCells = charsOf(before, need.rows, need.cols);
    const mode = contract.mode;
    const seedRows = contract.seedRows;
    const shape = cells => (mode === 'erase' ? cells : textsOf(cells));
    const beforeState = shape(beforeCells);
    const seeded = mode === 'erase'
        ? beforeCells.every((line, row) => line.every((char, col) => char === seedRows[row]?.[col]))
        : beforeState.join('|') === seedRows.join('|');
    if (!seeded) return parserState(null, notes.expected, { before: beforeState }, notes.seed);
    if (cursor.x !== 0 || cursor.y !== 0) return parserState(null, notes.expected, { before: beforeState, setup: cursor }, notes.cursor);
    const afterCells = charsOf(edited, need.rows, need.cols);
    const afterState = shape(afterCells);
    let controlsValid, verdict;
    if (mode === 'fill') {
        controlsValid = afterState.every((line, row) => line[5] === beforeState[row]?.[5]);
        verdict = controlsValid ? afterState.every(line => line.slice(0, 5) === 'XXXXX') : null;
    } else if (mode === 'erase') {
        controlsValid = afterState.every((line, row) => line[5] === beforeState[row]?.[5]);
        verdict = controlsValid ? afterState.every(line => line.slice(0, 5).every(isBlank)) : null;
    } else {
        controlsValid = afterState.slice(0, 4).every((line, row) => line === beforeState[row]) && afterState[4]?.slice(0, 9) === beforeState[4]?.slice(0, 9)
            && afterState[5]?.slice(0, 9) === beforeState[5]?.slice(0, 9);
        verdict = controlsValid ? afterState[4]?.slice(9) === 'ABCDE' && afterState[5]?.slice(9) === 'FGHIJ' : null;
    }
    return parserState(verdict, notes.expected, { before: beforeState, setup: cursor, after: afterState }, controlsValid ? undefined : notes.controls);
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export const buildRectEditGradingReport = (frames, run, runId, adapter, options) => buildColorReport(spec, frames, run, runId, adapter, options);
