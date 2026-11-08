// Parent-side report builder for the erase-in-line qualification. Re-derives every conclusion from the
// retained parent journal and the independent contract grader, refuses any tampering, and requires the
// pinned probe's own eraseRowResult grading to agree: the pinned result is a pure function of the observed
// screens of its own fixture. The contract may never be more permissive than the pinned probe; any difference
// in verdict semantics is recorded with both verdicts.
import { eraseLineContracts, eraseLineCaseIds, eraseLineContractsVersion, eraseLineNoncePrefix, eraseLineSuiteId,
    gradeEraseLineCase, eraseLineCellAt, eraseLineObservation, validateEraseLineScreen,
    pinnedGeometryNote, pinnedFixtureNote } from './erase-line-contracts.mjs';
import { colorScreenOf, validateColorEvidence, buildColorReport, incomplete, decided } from './sgr-color-grading-core.mjs';
import { eraseLineAdapterId } from './erase-line-grading-collector.mjs';
import { eraseLineRecordingContext } from './erase-line-collector.mjs';

/** The suite's report spec: identities, pinned replay and scope. */
const spec = Object.freeze({ label: 'Erase-in-line', prefix: 'erln', noncePrefix: eraseLineNoncePrefix,
    contracts: eraseLineContracts, caseIds: eraseLineCaseIds, suiteId: eraseLineSuiteId, version: eraseLineContractsVersion,
    adapterId: eraseLineAdapterId, derivedFrom: 'ntilde-owned-sgr-selective-reset-grading-v1',
    screenOf: colorScreenOf(validateEraseLineScreen),
    observation: eraseLineObservation, grade: gradeEraseLineCase,
    replayPinned: (contract, screens) => replayEraseLinePinned(contract, screens),
    recordingContext: eraseLineRecordingContext, replayFn: 'ntildeRunEraseLineTermless',
    scope: 'Pinned terminfo.dev@4.0.0 erase-in-line termless criteria (erase.line.right with the omitted-parameter spelling, erase.line.left with EL 1 and '
            + 'erase.line.all with EL 2) replayed byte-exactly on a disposable App and graded from parent-journaled readScreen stored cells, the cursor and '
            + 'stored attribute details, with controls for the unmodified seed (no-op), every other EL spelling (omitted, explicit 0, 1 and 2: wrong direction, '
            + 'accidental whole-line clearing), a wide-row control for unaffected rows and columns past column 5 and the cursor-cell boundary, and a bold-seeded '
            + 'attribute control (retained attributes required; background-color erase observed, not required). Stored state only: the pinned terminal pixel '
            + 'path is capture-only upstream and is not qualified; no stock grades.' });

/** Validate journal, run identity, binding stability and restoration. */
export const validateEraseLineEvidence = (frames, run, runId, adapter) => validateColorEvidence(spec, frames, run, runId, adapter);

/** The pinned rowCells projection: the stored character of the first five cells, or null when it is not a string. */
const rowCells = (screen, row) => Array.from({ length: 5 }, (_, col) => {
    const char = eraseLineCellAt(screen, row, col).char;
    return typeof char === 'string' ? char : null;
});
/** The pinned isBlank. */
const isBlank = char => char === '' || char === ' ';

/** Re-derive the complete pinned eraseRowResult over the setup and erased screens of the pinned fixture,
 * mirroring the pinned result shapes exactly. */
export function replayEraseLinePinned(contract, screens) {
    const setup = screens[contract.setupRead], erased = screens[contract.pinnedRead];
    const adjacentRow = contract.neighbor;
    const expectedCursorX = contract.neighbor ? undefined : 2;
    const rows = setup.rows;
    if (!Number.isSafeInteger(setup.cols) || setup.cols < 6 || !Number.isSafeInteger(rows) || rows < (adjacentRow ? 2 : 1)) {
        return incomplete(JSON.stringify({ cols: setup.cols, rows }), pinnedGeometryNote);
    }
    const before = rowCells(setup, 0);
    const neighborBefore = adjacentRow ? rowCells(setup, 1) : undefined;
    const cursorBefore = expectedCursorX === undefined ? undefined : { x: setup.cursorCol, y: setup.cursorRow };
    const after = rowCells(erased, 0);
    const neighborAfter = adjacentRow ? rowCells(erased, 1) : undefined;
    const response = JSON.stringify({ before, ...cursorBefore && { cursorBefore: { x: cursorBefore.x, y: cursorBefore.y } },
        after, ...adjacentRow && { neighborBefore, neighborAfter } });
    const fixtureReady = before.join('') === 'ABCDE' && (!adjacentRow || neighborBefore?.join('') === 'KEEP!')
        && (expectedCursorX === undefined || (cursorBefore?.x === expectedCursorX && cursorBefore?.y === 0));
    const measured = [...after, ...neighborAfter ?? []];
    if (!fixtureReady || measured.some(char => char === null)) {
        return { pass: false, response, note: pinnedFixtureNote,
            observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state' } };
    }
    const exact = contract.pinnedCells.every((char, col) => (char === 'blank' ? isBlank(after[col] ?? '\x00') : after[col] === char));
    const neighborPreserved = !adjacentRow || neighborAfter?.join('') === 'KEEP!';
    return decided(exact && neighborPreserved, contract, response);
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export const buildEraseLineGradingReport = (frames, run, runId, adapter, options) => buildColorReport(spec, frames, run, runId, adapter, options);
