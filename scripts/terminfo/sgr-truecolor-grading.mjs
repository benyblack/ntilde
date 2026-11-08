// Parent-side report builder for the SGR truecolor qualification. Re-derives
// every conclusion from the retained parent journal and the independent
// contract grader, refuses any tampering, and requires the pinned probe's own
// grading to agree: the pinned result is a pure function of the observed screen
// of its own fixture. The contract may never be more permissive than the pinned
// probe; any difference in verdict semantics is recorded with both verdicts.
import { sgrTruecolorContracts, sgrTruecolorCaseIds, sgrTruecolorContractsVersion, sgrTruecolorNoncePrefix, sgrTruecolorSuiteId,
    gradeSgrTruecolorCase, sgrTruecolorCellAt, sgrTruecolorObservation, validateSgrTruecolorScreen,
    pinnedTruecolorNotExposedNote, pinnedTruecolorReadbackNote, pinnedTruecolorMimicNote,
    pinnedTruecolorCalibrationNote } from './sgr-truecolor-contracts.mjs';
import { colorScreenOf, validateColorEvidence, buildColorReport, sameRgb, incomplete, decided } from './sgr-color-grading-core.mjs';
import { sgrTruecolorAdapterId } from './sgr-truecolor-grading-collector.mjs';
import { sgrTruecolorRecordingContext } from './sgr-truecolor-collector.mjs';

/** The suite's report spec: identities, pinned replay and scope. */
const spec = Object.freeze({ label: 'SGR-truecolor', prefix: 'sgtc', noncePrefix: sgrTruecolorNoncePrefix,
    contracts: sgrTruecolorContracts, caseIds: sgrTruecolorCaseIds, suiteId: sgrTruecolorSuiteId, version: sgrTruecolorContractsVersion,
    adapterId: sgrTruecolorAdapterId, derivedFrom: 'ntilde-owned-sgr-palette-grading-v1',
    screenOf: colorScreenOf(validateSgrTruecolorScreen), observation: sgrTruecolorObservation, grade: gradeSgrTruecolorCase,
    replayPinned: (contract, screens) => replaySgrTruecolorPinned(contract, screens),
    recordingContext: sgrTruecolorRecordingContext, replayFn: 'ntildeRunSgrTruecolorTermless',
    scope: 'Pinned terminfo.dev@4.0.0 truecolor foreground and background termless criteria replayed byte-exactly on a disposable App '
            + 'and graded from parent-journaled readScreen stored literal bytes (palette index and default marker cleared) together with '
            + 'App-resolved RGB bound to one recorded palette/theme state, with controls for the unaffected channel, unrelated '
            + 'attributes, DECSCNM, literal-versus-palette storage with equal appearance, boundary components and reset restoration. Only the semicolon 38/48;2;r;g;b form is qualified. '
            + 'Rendered glyph appearance is not claimed; no stock grades.' });

/** Validate journal, run identity, binding stability and restoration. */
export const validateSgrTruecolorEvidence = (frames, run, runId, adapter) => validateColorEvidence(spec, frames, run, runId, adapter);

/** Re-derive the pinned truecolorProbe termless result over its five cells. */
function replayTruecolor(contract, cells) {
    const [baseline, controlFirst, controlSecond, targetFirst, targetSecond] = cells;
    const response = JSON.stringify({ baseline, controlFirst, controlSecond, targetFirst, targetSecond });
    if (baseline.char !== 'C' || controlFirst.char !== 'A' || controlSecond.char !== 'B'
        || targetFirst.char !== 'X' || targetSecond.char !== 'Y') return incomplete(response, pinnedTruecolorNotExposedNote);
    const x = targetFirst[contract.channel], y = targetSecond[contract.channel];
    if (!x || !y) return incomplete(response, pinnedTruecolorReadbackNote);
    const preceding = controlSecond[contract.channel];
    if (!preceding || sameRgb(preceding, contract.first)) return incomplete(response, pinnedTruecolorMimicNote);
    if (sameRgb(x, contract.first) && sameRgb(y, contract.second)) return decided(true, contract, response);
    const a = controlFirst[contract.channel], b = controlSecond[contract.channel];
    if (a && b && !sameRgb(a, b)) return decided(false, contract, response);
    return incomplete(response, pinnedTruecolorCalibrationNote);
}

const replays = Object.freeze({ truecolor: replayTruecolor });

/** Re-derive the complete pinned termless result from the observed screen of the
 * pinned fixture, mirroring the pinned code's shapes exactly. */
export function replaySgrTruecolorPinned(contract, screens) {
    const replay = replays[contract.pinnedKind];
    if (!replay) throw new Error(`Unknown pinned kind for ${contract.caseId}.`);
    const cells = Array.from({ length: contract.fixtureCells }, (_, col) => sgrTruecolorCellAt(screens[contract.pinnedRead], 0, col));
    return replay(contract, cells);
}


/** Re-derive and cross-check every case; throw on any disagreement. */
export const buildSgrTruecolorGradingReport = (frames, run, runId, adapter, options) => buildColorReport(spec, frames, run, runId, adapter, options);
