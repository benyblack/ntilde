// Parent-side report builder for the character-editing qualification. Re-derives every conclusion from the
// retained parent journal and the independent contract grader, refuses any tampering, and requires the pinned
// probes' own grading to agree: each pinned result is a pure function of the observed screens of its own fixture.
// The contract may never be more permissive than the pinned probe; any difference in verdict semantics is
// recorded with both verdicts.
import { charEditContracts, charEditCaseIds, charEditContractsVersion, charEditNoncePrefix, charEditSuiteId,
    gradeCharEditCase, charEditCellAt, charEditObservation, validateCharEditScreen } from './char-edit-contracts.mjs';
import { colorScreenOf, validateColorEvidence, buildColorReport, incomplete, decided } from './sgr-color-grading-core.mjs';
import { charEditAdapterId } from './char-edit-grading-collector.mjs';
import { charEditRecordingContext } from './char-edit-collector.mjs';

/** The suite's report spec: identities, pinned replay and scope. */
const spec = Object.freeze({ label: 'Character-editing', prefix: 'ched', noncePrefix: charEditNoncePrefix,
    contracts: charEditContracts, caseIds: charEditCaseIds, suiteId: charEditSuiteId, version: charEditContractsVersion,
    adapterId: charEditAdapterId, derivedFrom: 'ntilde-owned-erase-line-grading-v1',
    screenOf: colorScreenOf(validateCharEditScreen),
    observation: charEditObservation, grade: gradeCharEditCase,
    replayPinned: (contract, screens) => replayCharEditPinned(contract, screens),
    recordingContext: charEditRecordingContext, replayFn: 'ntildeRunCharEditTermless',
    scope: 'Pinned terminfo.dev@4.0.0 character-editing termless criteria (editing.insert-chars with the explicit-1 ICH spelling, editing.delete-chars with the '
            + 'explicit-1 DCH spelling and editing.repeat-char with REP count 3) replayed on a disposable App and graded from parent-journaled readScreen stored cells, '
            + 'the cursor and stored attribute details, with controls for the unmodified seed (no-op), every other count spelling (omitted, explicit 0, 2 and a huge count '
            + 'for ICH/DCH; omitted, 0, 1 and 5 for REP), a wide-row control for unaffected rows and the whole-row shift, a right-edge control (truncation and exposed '
            + 'blanks), REP after a fence barrier (ECMA-48 undefined) as a labeled observation, and an attribute control (moved cells keep their attributes; created '
            + 'blank cells and repeated cells observed against a same-pen reference, not required). REP sends the graphic and the repeat in one fenced write because the '
            + 'fence barrier is itself a control function; the pinned pre-REP reads come from a separate group ending at the graphic. Stored ASCII state only: the pinned '
            + 'pixel/query paths never measure cells and are not qualified; no Unicode, wide-cell or combining-character editing; no stock grades.' });

/** Validate journal, run identity, binding stability and restoration. */
export const validateCharEditEvidence = (frames, run, runId, adapter) => validateColorEvidence(spec, frames, run, runId, adapter);

/** The pinned getCell char projection of the first `count` cells of row 0. */
const cellsOf = (screen, count) => Array.from({ length: count }, (_, col) => charEditCellAt(screen, 0, col).char);
/** The pinned isBlank. */
const isBlank = char => char === '' || char === ' ';
/** The pinned getCursor projection. */
const cursorOf = screen => ({ x: screen.cursorCol, y: screen.cursorRow, visible: screen.cursorVisible });

/** Re-derive the pinned ICH/DCH result over the screens its feeds were read from (seed, cursor, edited). */
function replayShift(contract, screens) {
    const [seedScreen, cursorScreen, editedScreen] = contract.pinnedReads.map(index => screens[index]);
    const notes = contract.notes, ich = contract.op === 'ich';
    if (!Number.isSafeInteger(seedScreen.cols) || seedScreen.cols < 8) return incomplete(JSON.stringify({ cols: seedScreen.cols }), notes.geometry);
    const before = cellsOf(seedScreen, 8);
    if (before.join('') !== 'ABCDEZQH') return incomplete(JSON.stringify({ before }), notes.seed);
    const setup = cursorOf(cursorScreen);
    if (setup.x !== 2 || setup.y !== 0) return incomplete(JSON.stringify({ before, setup }), notes.cursor);
    const after = cellsOf(editedScreen, ich ? 8 : 7);
    const response = JSON.stringify({ before, setup, after });
    if (!(after[0] === 'A' && after[1] === 'B')) return incomplete(response, notes.control);
    return decided(ich ? isBlank(after[2] ?? '') && after.slice(3).join('') === 'CDEZQ' : after.slice(2).join('') === 'DEZQH', contract, response);
}

/** Re-derive the pinned REP result over the screens its feeds were read from (seed, target, before, repeated). */
function replayRepeat(contract, screens) {
    const [seedScreen, targetScreen, beforeScreen, repeatedScreen] = contract.pinnedReads.map(index => screens[index]);
    const notes = contract.notes;
    if (!Number.isSafeInteger(seedScreen.cols) || seedScreen.cols < 6) return incomplete(JSON.stringify({ cols: seedScreen.cols }), notes.geometry);
    const seed = cellsOf(seedScreen, 6);
    if (seed.join('') !== 'AX   Z') return incomplete(JSON.stringify({ seed }), notes.seed);
    const targetPosition = cursorOf(targetScreen);
    if (targetPosition.x !== 1 || targetPosition.y !== 0) return incomplete(JSON.stringify({ seed, targetPosition }), notes.cursor);
    const before = cellsOf(beforeScreen, 6);
    const setup = cursorOf(beforeScreen);
    if (before.join('') !== 'AX   Z' || setup.x !== 2 || setup.y !== 0) return incomplete(JSON.stringify({ seed, targetPosition, before, setup }), notes.setup);
    const after = cellsOf(repeatedScreen, 6);
    const response = JSON.stringify({ seed, targetPosition, before, setup, after });
    if (!(after[0] === 'A' && after[5] === 'Z')) return incomplete(response, notes.control);
    return decided(after.slice(1, 5).join('') === 'XXXX', contract, response);
}

/** Re-derive the complete pinned termless result over the pinned fixture's observed screens, mirroring the pinned result shapes exactly. */
export const replayCharEditPinned = (contract, screens) => (contract.family === 'repeat' ? replayRepeat(contract, screens) : replayShift(contract, screens));

/** Re-derive and cross-check every case; throw on any disagreement. */
export const buildCharEditGradingReport = (frames, run, runId, adapter, options) => buildColorReport(spec, frames, run, runId, adapter, options);
