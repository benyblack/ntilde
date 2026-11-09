// Parent-side report builder for the erase-with-attributes / erase-with-margins qualification. Re-derives every conclusion from the
// retained parent journal and the independent contract grader, refuses any tampering, and requires the pinned probes' own grading
// to agree: each pinned result is a pure function of the observed screens of its own fixture. The contract may never be more
// permissive than the pinned probe; any difference in verdict semantics is recorded with both verdicts.
import { eraseAttrsRegionContracts, eraseAttrsRegionCaseIds, eraseAttrsRegionContractsVersion, eraseAttrsRegionNoncePrefix, eraseAttrsRegionSuiteId,
    gradeEraseAttrsRegionCase, eraseAttrsRegionCellAt, eraseAttrsRegionObservation, eraseAttrsRegionScrollback, validateEraseAttrsRegionScreen,
    pinnedElSetupNote, pinnedElUnavailableNote, pinnedEdGeometryNote, pinnedEdSetupNote, pinnedEdPreservedNote } from './erase-attrs-region-contracts.mjs';
import { validateColorEvidence, buildColorReport } from './sgr-color-grading-core.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { eraseAttrsRegionAdapterId } from './erase-attrs-region-grading-collector.mjs';
import { eraseAttrsRegionRecordingContext } from './erase-attrs-region-collector.mjs';

/** Erase screens carry attributes, stored details (with overline), resolved colors, buffer state and history together. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, false, true, true, true);
    validateEraseAttrsRegionScreen(screen);
    return screen;
};

/** The suite's report spec: identities, pinned replay and scope. */
const spec = Object.freeze({ label: 'Erase attributes and margins', prefix: 'erar', noncePrefix: eraseAttrsRegionNoncePrefix,
    contracts: eraseAttrsRegionContracts, caseIds: eraseAttrsRegionCaseIds, suiteId: eraseAttrsRegionSuiteId, version: eraseAttrsRegionContractsVersion,
    adapterId: eraseAttrsRegionAdapterId, derivedFrom: 'ntilde-owned-saved-reset-grading-v1',
    screenOf,
    observation: eraseAttrsRegionObservation, grade: gradeEraseAttrsRegionCase,
    replayPinned: (contract, screens) => replayEraseAttrsRegionPinned(contract, screens),
    recordingContext: eraseAttrsRegionRecordingContext, replayFn: 'ntildeRunEraseAttrsRegionTermless',
    scope: 'Pinned terminfo.dev@4.0.0 termless criteria erase.el-with-attrs (EL, omitted parameter, over a measured SGR 42 background: the erased cell (0,0) is blank with the same '
            + 'resolved background RGB) and erase.ed-scroll-region (ED, omitted parameter, with margins 3;10 and the cursor at row 2: the preceding row KEEP! survives and the target cells read blank) '
            + 'replayed byte-exactly on a disposable App and graded from parent-journaled readScreen stored cells, App-resolved colors, the cursor, the margins and buffer state, '
            + 'with a colored-X seed proof, a restoration witness (pen and margins), a full-screen control against accidental whole-screen clearing, a stored-representation versus '
            + 'resolved-color comparison, current-pen versus preserved-background controls (observed, not required) and a marker fill that records where ED stopped relative to the margins '
            + '(observed, not graded: no claim that ED is or is not clipped). Stored state and App-resolved color only: the pinned terminal cursor-query paths never carry a verdict and are '
            + 'not qualified; rendered appearance is not claimed; no stock grades.' });

/** Validate journal, run identity, binding stability and restoration. */
export const validateEraseAttrsRegionEvidence = (frames, run, runId, adapter) => validateColorEvidence(spec, frames, run, runId, adapter);

/** The pinned isBlank. */
const isBlank = char => char === '' || char === ' ';
/** The pinned rowCells projection: the stored character of the first five cells, or null when it is not a string. */
const rowCells = (screen, row) => Array.from({ length: 5 }, (_, col) => {
    const char = eraseAttrsRegionCellAt(screen, row, col).char;
    return typeof char === 'string' ? char : null;
});
/** The pinned parserStateResult, key order included (the retained pinned result is compared structurally). */
function parserState(pass, expected, state, note) {
    const response = JSON.stringify(state);
    return {
        pass: pass === true, response, ...note && { note },
        observation: { outcome: pass === null ? 'inconclusive' : pass ? 'supported' : 'unsupported',
            ...pass === null && { reason: 'insufficient-evidence' }, evidence: 'parser-state', ...note && { note } },
        ...pass !== null && { assertions: [{ kind: pass ? 'positive' : 'negative', expected, observed: response }] },
    };
}

/** Re-derive the complete pinned erase.el-with-attrs result over the setup and erased screens of the pinned fixture. */
function replayEl(contract, screens) {
    const before = eraseAttrsRegionCellAt(screens[contract.setupRead], 0, 0);
    if (before.char !== 'X' || before.bg == null) return parserState(null, contract.pinnedExpected, { before }, pinnedElSetupNote);
    const after = eraseAttrsRegionCellAt(screens[contract.pinnedRead], 0, 0);
    const measured = typeof after.char === 'string' && after.bg != null;
    const pass = measured ? isBlank(after.char) && after.bg?.r === before.bg.r && after.bg?.g === before.bg.g && after.bg?.b === before.bg.b : null;
    return parserState(pass, contract.pinnedExpected, { before, after }, measured ? undefined : pinnedElUnavailableNote);
}

/** Re-derive the complete pinned erase.ed-scroll-region result over the initial, setup and erased screens of the pinned fixture. */
function replayEd(contract, screens) {
    const initial = screens[contract.initialRead], setup = screens[contract.setupRead], erased = screens[contract.pinnedRead];
    const rows = eraseAttrsRegionScrollback(initial).screenLines, cols = setup.cols;
    if (!Number.isSafeInteger(rows) || rows < 10 || !Number.isSafeInteger(cols) || cols < 10) {
        return parserState(null, contract.pinnedExpected, { rows, cols }, pinnedEdGeometryNote);
    }
    const before = { control: rowCells(setup, 0), target: rowCells(setup, 2), cursor: { x: setup.cursorCol, y: setup.cursorRow, visible: setup.cursorVisible } };
    if (before.control.join('') !== 'KEEP!' || before.target.join('') !== 'ERASE' || before.cursor.x !== 0 || before.cursor.y !== 2) {
        return parserState(null, contract.pinnedExpected, { before }, pinnedEdSetupNote);
    }
    const after = { control: rowCells(erased, 0), target: rowCells(erased, 2) };
    const valid = after.control.join('') === 'KEEP!' && after.target.every(char => char !== null);
    return parserState(valid ? after.target.every(char => char !== null && isBlank(char)) : null, contract.pinnedExpected, { before, after },
        valid ? undefined : pinnedEdPreservedNote);
}

/** Re-derive the complete pinned result of one case over the screens of its fixture, mirroring the pinned result shapes exactly. */
export function replayEraseAttrsRegionPinned(contract, screens) {
    if (contract.caseId === 'erase.el-with-attrs') return replayEl(contract, screens);
    if (contract.caseId === 'erase.ed-scroll-region') return replayEd(contract, screens);
    throw new Error(`Unknown erase-attributes case ${contract.caseId}.`);
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export const buildEraseAttrsRegionGradingReport = (frames, run, runId, adapter, options) => buildColorReport(spec, frames, run, runId, adapter, options);
