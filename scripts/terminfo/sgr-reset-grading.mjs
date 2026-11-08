// Parent-side report builder for the SGR selective-reset qualification. Re-derives
// every conclusion from the retained parent journal and the independent
// contract grader, refuses any tampering, and requires the pinned probe's own
// measuredReset grading to agree: the pinned result is a pure function of the
// observed screen of its own fixture. The contract may never be more permissive
// than the pinned probe; any difference in verdict semantics is recorded with
// both verdicts.
import { sgrResetContracts, sgrResetCaseIds, sgrResetContractsVersion, sgrResetNoncePrefix, sgrResetSuiteId,
    gradeSgrResetCase, sgrResetCellAt, sgrResetObservation, validateSgrResetScreen,
    pinnedNotExposedNote, pinnedControlNote } from './sgr-reset-contracts.mjs';
import { colorScreenOf, validateColorEvidence, buildColorReport, incomplete, decided } from './sgr-color-grading-core.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { planReads } from './graded-plan.mjs';
import { sgrResetAdapterId } from './sgr-reset-grading-collector.mjs';
import { sgrResetRecordingContext } from './sgr-reset-collector.mjs';

/** Everything in a binding except DECSCNM and the theme defaults it folds in. */
const themeKey = binding => JSON.stringify({ ...binding, reverseVideo: undefined, foregroundArgb: undefined, backgroundArgb: undefined });

/** The journal index of the one frame allowed to carry DECSCNM on: the contracted control read of the inverse case. */
function reverseFrameIndex() {
    let index = 2;
    for (const contract of sgrResetContracts) {
        if (contract.reverseControlRead !== undefined) return index + contract.reverseControlRead;
        index += planReads(contract);
    }
    return -1;
}

/** The theme and palette never change; DECSCNM is off in every frame except the contracted control read, which must have it
 * on; and the run ends on its start binding. */
function bindingStable(frames) {
    const start = frames[0].screen.colorBinding;
    const key = themeKey(start);
    const reverseAt = reverseFrameIndex();
    for (const [index, frame] of frames.entries()) {
        const binding = frame.screen.colorBinding;
        if (themeKey(binding) !== key) throw new Error('SGR selective-reset observation saw the theme or palette change.');
        if (index === reverseAt) {
            if (binding.reverseVideo !== true) throw new Error('SGR selective-reset DECSCNM control frame did not report reverse video.');
            // DECSCNM folds into the theme defaults by swapping them; any other default-color change is not reverse video.
            if (binding.foregroundArgb !== start.backgroundArgb || binding.backgroundArgb !== start.foregroundArgb) {
                throw new Error('SGR selective-reset DECSCNM control frame changed the theme defaults beyond the reverse-video swap.');
            }
        } else if (colorBindingHash(binding) !== colorBindingHash(start)) {
            throw new Error('SGR selective-reset observation saw an unexpected binding change.');
        }
    }
}

/** The suite's report spec: identities, pinned replay and scope. */
const spec = Object.freeze({ label: 'SGR selective-reset', prefix: 'sgsr', noncePrefix: sgrResetNoncePrefix,
    contracts: sgrResetContracts, caseIds: sgrResetCaseIds, suiteId: sgrResetSuiteId, version: sgrResetContractsVersion,
    adapterId: sgrResetAdapterId, derivedFrom: 'ntilde-owned-sgr-underline-color-grading-v1',
    screenOf: colorScreenOf(validateSgrResetScreen), bindingStable,
    observation: sgrResetObservation, grade: gradeSgrResetCase,
    replayPinned: (contract, screens) => replaySgrResetPinned(contract, screens),
    recordingContext: sgrResetRecordingContext, replayFn: 'ntildeRunSgrResetTermless',
    scope: 'Pinned terminfo.dev@4.0.0 selective-reset underline (SGR 24) and inverse (SGR 27) termless criteria replayed byte-exactly on a disposable App '
            + 'and graded from parent-journaled readScreen stored attribute flags, the exact stored underline style, stored foreground/background and '
            + 'overline, with controls for initially inactive attributes, the same bytes without the reset (no-op), an accidental full SGR 0, a wrong '
            + 'reset sequence, unrelated-attribute and color drift, DECSCNM (proven off, and a DECSCNM-on control for per-cell inverse) and, for '
            + 'underline, double-underline, SGR 59 and observed (not required) underline-color behavior. Stored state only: rendered appearance '
            + 'is not claimed; no stock grades.' });

/** Validate journal, run identity, binding stability and restoration. */
export const validateSgrResetEvidence = (frames, run, runId, adapter) => validateColorEvidence(spec, frames, run, runId, adapter);

/** The pinned measuredAttribute: undefined is unmeasured, underline is truthy unless false/null/"none", others must be boolean. */
function measuredAttribute(cell, attribute) {
    const value = cell[attribute];
    if (value === undefined) return null;
    if (attribute === 'underline') return value !== false && value !== null && value !== 'none';
    return typeof value === 'boolean' ? value : null;
}

/** Re-derive the complete pinned measuredReset result over the three cells of the pinned fixture,
 * mirroring the pinned parserStateResult shapes exactly. */
export function replaySgrResetPinned(contract, screens) {
    const [baseline, before, after] = [0, 1, 2].map(col => sgrResetCellAt(screens[contract.pinnedRead], 0, col));
    const response = JSON.stringify({ baseline, before, after });
    if (baseline.char !== 'C' || before.char !== 'X' || after.char !== 'Y') return incomplete(response, pinnedNotExposedNote);
    const attributes = [...new Set([...contract.set, ...contract.preserve])];
    if (attributes.some(name => measuredAttribute(baseline, name) !== false) || attributes.some(name => measuredAttribute(before, name) !== true)
        || attributes.some(name => measuredAttribute(after, name) === null)) {
        return incomplete(response, pinnedControlNote);
    }
    const cleared = contract.set.every(name => measuredAttribute(after, name) === false);
    const retained = contract.preserve.every(name => measuredAttribute(after, name) === true);
    return decided(cleared && retained, contract, response);
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export const buildSgrResetGradingReport = (frames, run, runId, adapter, options) => buildColorReport(spec, frames, run, runId, adapter, options);
