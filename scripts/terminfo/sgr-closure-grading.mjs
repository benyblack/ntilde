// Parent-side report builder for the SGR closure qualification. Re-derives every conclusion from the retained parent
// journal and the independent contract grader, refuses any tampering, and requires the pinned probe's own grading to
// agree: the pinned result is a pure function of the observed screen of its own fixture. The contract may never be more
// permissive than the pinned probe; any difference in verdict semantics is recorded with both verdicts. The
// sgr.underline.color case is replayed under its OWN id over its OWN reads; the sibling sgr.underline-color-rgb verdict
// is never consulted.
import { sgrClosureContracts, sgrClosureCaseIds, sgrClosureContractsVersion, sgrClosureNoncePrefix, sgrClosureSuiteId,
    gradeSgrClosureCase, sgrClosureCellAt, sgrClosureObservation, validateSgrClosureScreen,
    pinnedNoRenderedCellNote, pinnedOverlineUnavailableNote, pinnedUnreportedNote } from './sgr-closure-contracts.mjs';
import { colorScreenOf, validateColorEvidence, buildColorReport, incomplete, decided } from './sgr-color-grading-core.mjs';
import { replaySgrUnderlineColorPinned } from './sgr-underline-color-grading.mjs';
import { sgrClosureAdapterId } from './sgr-closure-grading-collector.mjs';
import { sgrClosureRecordingContext } from './sgr-closure-collector.mjs';

/** The pinned parserStateResult/notTestedResult shapes for the generic sgrProbe termless body. */
export function replaySgrProbeCell(contract, cell) {
    const response = JSON.stringify(cell);
    if (cell.char === 'X' && contract.caseId === 'sgr.overline' && cell.overline === undefined) {
        // notTestedResult: the pinned no-semantic-observable guard for an UNAVAILABLE overline field.
        return { pass: false, response, note: pinnedOverlineUnavailableNote,
            notTested: { reason: 'no-semantic-observable', noObservable: pinnedOverlineUnavailableNote } };
    }
    const check = contract.caseId === 'sgr.blink' ? cell.blink === true : cell.overline === undefined ? null : cell.overline === true;
    const measured = cell.char === 'X' && check === true;
    if (measured) return decided(true, contract, response);
    return incomplete(response, cell.char !== 'X' ? pinnedNoRenderedCellNote : pinnedUnreportedNote);
}

/** Re-derive the complete pinned termless result from the observed screen of the pinned fixture. */
export function replaySgrClosurePinned(contract, screens) {
    if (contract.kind === 'underline-color') {
        // Same pinned helper as the sibling; the criterion text carries this case's own id through contract.pinnedExpected.
        return replaySgrUnderlineColorPinned({ ...contract, caseId: contract.siblingCaseId }, screens);
    }
    return replaySgrProbeCell(contract, sgrClosureCellAt(screens[contract.pinnedRead], 0, 0));
}

/** The suite's report spec: identities, pinned replay and scope. */
const spec = Object.freeze({ label: 'SGR closure', prefix: 'sgcl', noncePrefix: sgrClosureNoncePrefix,
    contracts: sgrClosureContracts, caseIds: sgrClosureCaseIds, suiteId: sgrClosureSuiteId, version: sgrClosureContractsVersion,
    adapterId: sgrClosureAdapterId, derivedFrom: 'ntilde-owned-sgr-selective-reset-grading-v1',
    screenOf: colorScreenOf(validateSgrClosureScreen),
    observation: sgrClosureObservation, grade: gradeSgrClosureCase,
    replayPinned: (contract, screens) => replaySgrClosurePinned(contract, screens),
    recordingContext: sgrClosureRecordingContext, replayFn: 'ntildeRunSgrClosureTermless',
    scope: 'Pinned terminfo.dev@4.0.0 sgr.blink, sgr.overline and sgr.underline.color termless criteria replayed byte-exactly on a disposable App '
            + 'and graded from parent-journaled readScreen stored attribute flags, the explicit stored overline boolean, underline style and stored '
            + 'underline color (index, literal bytes, default null marker) with parent-derived RGB, with controls for neutral setup, reset restoration, '
            + 'wrong-flag decoys, overline independence from underline, unrelated-attribute and color drift, DECSCNM and, for the color case, the '
            + 'sibling-shared literal/index/foreground controls. Stored state only: no blink timing or animation, no overline or underline stroke '
            + 'appearance, no App-resolved color; no stock grades.' });

/** Validate journal, run identity, binding stability and restoration. */
export const validateSgrClosureEvidence = (frames, run, runId, adapter) => validateColorEvidence(spec, frames, run, runId, adapter);

/** Re-derive and cross-check every case; throw on any disagreement. */
export const buildSgrClosureGradingReport = (frames, run, runId, adapter, options) => buildColorReport(spec, frames, run, runId, adapter, options);
