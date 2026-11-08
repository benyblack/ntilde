// Parent-side report builder for the SGR underline-style qualification. Re-derives
// every conclusion from the retained parent journal and the independent
// contract grader, refuses any tampering, and requires the pinned probe's own
// grading to agree: the pinned result is a pure function of the observed screen
// of its own fixture. The contract may never be more permissive than the pinned
// probe; any difference in verdict semantics is recorded with both verdicts.
import { sgrUnderlineContracts, sgrUnderlineCaseIds, sgrUnderlineContractsVersion, sgrUnderlineNoncePrefix, sgrUnderlineSuiteId,
    gradeSgrUnderlineCase, sgrUnderlineCellAt, sgrUnderlineObservation, validateSgrUnderlineScreen,
    pinnedNoRenderedCellNote, pinnedUnreportedNote } from './sgr-underline-contracts.mjs';
import { colorScreenOf, validateColorEvidence, buildColorReport, incomplete, decided } from './sgr-color-grading-core.mjs';
import { sgrUnderlineAdapterId } from './sgr-underline-grading-collector.mjs';
import { sgrUnderlineRecordingContext } from './sgr-underline-collector.mjs';

/** The suite's report spec: identities, pinned replay and scope. */
const spec = Object.freeze({ label: 'SGR-underline', prefix: 'sgus', noncePrefix: sgrUnderlineNoncePrefix,
    contracts: sgrUnderlineContracts, caseIds: sgrUnderlineCaseIds, suiteId: sgrUnderlineSuiteId, version: sgrUnderlineContractsVersion,
    adapterId: sgrUnderlineAdapterId, derivedFrom: 'ntilde-owned-sgr-truecolor-grading-v1',
    screenOf: colorScreenOf(validateSgrUnderlineScreen),
    observation: sgrUnderlineObservation, grade: gradeSgrUnderlineCase,
    replayPinned: (contract, screens) => replaySgrUnderlinePinned(contract, screens),
    recordingContext: sgrUnderlineRecordingContext, replayFn: 'ntildeRunSgrUnderlineTermless',
    scope: 'Pinned terminfo.dev@4.0.0 double, curly, dotted and dashed underline-style termless criteria replayed byte-exactly on a disposable App '
            + 'and graded from parent-journaled readScreen stored attribute flags and the exact stored underline-style value, with controls for '
            + 'initially inactive attributes, unrelated-attribute and color drift, DECSCNM, SGR 24 restoration and a labeled ladder of every '
            + 'underline spelling (SGR 4, 4:1, 21, 4:2, 4:3, 4:4, 4:5) that proves the readback can tell styles apart. '
            + 'Stored state only: stroke appearance is not claimed; no stock grades.' });

/** Validate journal, run identity, binding stability and restoration. */
export const validateSgrUnderlineEvidence = (frames, run, runId, adapter) => validateColorEvidence(spec, frames, run, runId, adapter);

/** Re-derive the complete pinned sgrProbe termless result from the observed screen of the
 * pinned fixture, mirroring the pinned code's shapes exactly: the pinned probe grades
 * supported or inconclusive, never unsupported. */
export function replaySgrUnderlinePinned(contract, screens) {
    const cell = sgrUnderlineCellAt(screens[contract.pinnedRead], 0, 0);
    const response = JSON.stringify(cell);
    if (cell.char !== 'X') return incomplete(response, pinnedNoRenderedCellNote);
    if (contract.pinnedCheck(cell) !== true) return incomplete(response, pinnedUnreportedNote);
    return decided(true, contract, response);
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export const buildSgrUnderlineGradingReport = (frames, run, runId, adapter, options) => buildColorReport(spec, frames, run, runId, adapter, options);
