// Parent-side report builder for the SGR underline-color qualification. Re-derives
// every conclusion from the retained parent journal and the independent
// contract grader, refuses any tampering, and requires the pinned probe's own
// grading to agree: the pinned result is a pure function of the observed screen
// of its own fixture. The contract may never be more permissive than the pinned
// probe; any difference in verdict semantics is recorded with both verdicts.
import { sgrUnderlineColorContracts, sgrUnderlineColorCaseIds, sgrUnderlineColorContractsVersion, sgrUnderlineColorNoncePrefix,
    sgrUnderlineColorSuiteId, gradeSgrUnderlineColorCase, sgrUnderlineColorCellAt, sgrUnderlineColorObservation,
    validateSgrUnderlineColorScreen, requestedUnderlineColor, pinnedIndexedNotExposedNote, pinnedIndexedIndistinguishableNote,
    pinnedUncalibratedNote, pinnedRgbNotExposedNote, pinnedRgbDefaultMatchesNote, pinnedResetNotExposedNote,
    pinnedResetPrerequisiteNote, pinnedResetRepresentationNote } from './sgr-underline-color-contracts.mjs';
import { colorScreenOf, validateColorEvidence, buildColorReport, sameRgb, incomplete, decided } from './sgr-color-grading-core.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { sgrUnderlineColorAdapterId } from './sgr-underline-color-grading-collector.mjs';
import { sgrUnderlineColorRecordingContext } from './sgr-underline-color-collector.mjs';

/** Everything in a binding except the palette and its override list. */
const themeKey = binding => JSON.stringify({ ...binding, ansiPaletteArgb: undefined, paletteOverrideIndices: undefined });

/** The theme may not change; only the contracted palette entries may, inside the indexed case's mutation reads, and the run ends on its start palette. */
function bindingStable(frames) {
    const theme = themeKey(frames[0].screen.colorBinding);
    if (frames.some(frame => themeKey(frame.screen.colorBinding) !== theme)) {
        throw new Error('SGR-underline-color observation saw the theme change.');
    }
    if (colorBindingHash(frames[0].screen.colorBinding) !== colorBindingHash(frames.at(-1).screen.colorBinding)) {
        throw new Error('SGR-underline-color palette was not restored.');
    }
}

/** The suite's report spec: identities, pinned replay and scope. */
const spec = Object.freeze({ label: 'SGR-underline-color', prefix: 'sguc', noncePrefix: sgrUnderlineColorNoncePrefix,
    contracts: sgrUnderlineColorContracts, caseIds: sgrUnderlineColorCaseIds, suiteId: sgrUnderlineColorSuiteId,
    version: sgrUnderlineColorContractsVersion, adapterId: sgrUnderlineColorAdapterId, derivedFrom: 'ntilde-owned-sgr-underline-grading-v1',
    screenOf: colorScreenOf(validateSgrUnderlineColorScreen), bindingStable,
    observation: sgrUnderlineColorObservation, grade: gradeSgrUnderlineColorCase,
    replayPinned: (contract, screens) => replaySgrUnderlineColorPinned(contract, screens),
    recordingContext: sgrUnderlineColorRecordingContext, replayFn: 'ntildeRunSgrUnderlineColorTermless',
    scope: 'Pinned terminfo.dev@4.0.0 indexed, RGB and reset underline-color termless criteria replayed byte-exactly on a disposable App '
            + 'and graded from parent-journaled readScreen stored underline style, underline color index / literal ARGB / default marker, '
            + 'with the resolved underline RGB derived parent-side from the stored color and the recorded palette binding, and controls for '
            + 'unrelated attribute, overline and foreground/background drift, DECSCNM, literal-versus-index storage with equal RGB, '
            + 'foreground substitution, explicit-versus-default, style preservation across SGR 59, and (indexed) an OSC 4 mutation of '
            + 'every contracted entry with a full-palette comparison and restoration. Stored state and parent-derived resolution only: '
            + 'rendered underline appearance is not claimed; no stock grades. The aggregate sgr.underline.color case is not qualified here.' });

/** Validate journal, run identity, binding stability and restoration. */
export const validateSgrUnderlineColorEvidence = (frames, run, runId, adapter) => validateColorEvidence(spec, frames, run, runId, adapter);

/** Re-derive the pinned indexedUnderlineColor termless result over its four cells. */
function replayIndexed(contract, cells) {
    const [default4, target5, default5, target4] = cells;
    const response = JSON.stringify({ default4, target5, default5, target4 });
    if (default4.char !== 'A' || target5.char !== 'X' || default5.char !== 'B' || target4.char !== 'Y'
        || !default4.underline || !target5.underline || !default5.underline || !target4.underline
        || !default4.fg || !default5.fg || target5.underlineColor == null || target4.underlineColor == null) {
        return incomplete(response, pinnedIndexedNotExposedNote);
    }
    if (sameRgb(default4.fg, default5.fg)) return incomplete(response, pinnedIndexedIndistinguishableNote);
    const matches = sameRgb(default5.fg, target5.underlineColor) && sameRgb(default4.fg, target4.underlineColor)
        && (!default4.underlineColor || !sameRgb(default4.underlineColor, target5.underlineColor))
        && (!default5.underlineColor || !sameRgb(default5.underlineColor, target4.underlineColor));
    if (matches) return decided(true, contract, response);
    if (default4.underlineColor && default5.underlineColor && !sameRgb(default4.underlineColor, default5.underlineColor)) {
        return decided(false, contract, response);
    }
    return incomplete(response, pinnedUncalibratedNote);
}

/** Re-derive the pinned rgbUnderlineProbe termless result over its three cells. */
function replayRgb(contract, cells) {
    const [blueControl, greenControl, target] = cells;
    const response = JSON.stringify({ blueControl, greenControl, target });
    if (blueControl.char !== 'A' || greenControl.char !== 'B' || target.char !== 'X' || !blueControl.underline
        || !greenControl.underline || !target.underline || target.underlineColor == null) {
        return incomplete(response, pinnedRgbNotExposedNote);
    }
    if (greenControl.underlineColor && sameRgb(greenControl.underlineColor, requestedUnderlineColor)) {
        return incomplete(response, pinnedRgbDefaultMatchesNote);
    }
    if (sameRgb(target.underlineColor, requestedUnderlineColor)) return decided(true, contract, response);
    if (blueControl.fg && greenControl.fg && !sameRgb(blueControl.fg, greenControl.fg) && blueControl.underlineColor
        && greenControl.underlineColor && !sameRgb(blueControl.underlineColor, greenControl.underlineColor)) {
        return decided(false, contract, response);
    }
    return incomplete(response, pinnedUncalibratedNote);
}

/** Re-derive the pinned reset probe termless result over its three cells. */
function replayReset(contract, cells) {
    const [baseline, before, after] = cells;
    const response = JSON.stringify({ baseline, before, after });
    if (baseline.char !== 'C' || before.char !== 'X' || after.char !== 'Y' || !baseline.underline || !before.underline
        || !after.underline || baseline.underlineColor === undefined || before.underlineColor === undefined
        || after.underlineColor === undefined) {
        return incomplete(response, pinnedResetNotExposedNote);
    }
    if (!before.underlineColor || !sameRgb(before.underlineColor, requestedUnderlineColor)
        || baseline.underlineColor && sameRgb(baseline.underlineColor, requestedUnderlineColor)) {
        return incomplete(response, pinnedResetPrerequisiteNote);
    }
    if (after.underlineColor && sameRgb(after.underlineColor, before.underlineColor)) return decided(false, contract, response);
    if (baseline.underlineColor === null || after.underlineColor === null) {
        if (baseline.underlineColor === after.underlineColor) return decided(true, contract, response);
        return incomplete(response, pinnedResetRepresentationNote);
    }
    return decided(sameRgb(baseline.underlineColor, after.underlineColor), contract, response);
}

const replays = Object.freeze({ 'sgr.underline-color-indexed': replayIndexed, 'sgr.underline-color-rgb': replayRgb,
    'sgr.underline-color-reset': replayReset });

/** Re-derive the complete pinned termless result from the observed screen of the
 * pinned fixture, mirroring the pinned code's shapes exactly. */
export function replaySgrUnderlineColorPinned(contract, screens) {
    const replay = replays[contract.caseId];
    if (!replay) throw new Error(`Unknown pinned case ${contract.caseId}.`);
    const cells = Array.from({ length: contract.fixtureCells }, (_, col) => sgrUnderlineColorCellAt(screens[contract.pinnedRead], 0, col));
    return replay(contract, cells);
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export const buildSgrUnderlineColorGradingReport = (frames, run, runId, adapter, options) => buildColorReport(spec, frames, run, runId, adapter, options);
