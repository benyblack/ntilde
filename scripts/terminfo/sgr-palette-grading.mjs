// Parent-side report builder for the SGR palette-color qualification. Re-derives
// every conclusion from the retained parent journal and the independent
// contract grader, refuses any tampering, and requires the pinned probe's own
// grading to agree: the pinned result is a pure function of the observed screen
// of its own fixture. The contract may never be more permissive than the pinned
// probe; any difference in verdict semantics is recorded with both verdicts.
import { sgrPaletteContracts, sgrPaletteCaseIds, sgrPaletteContractsVersion, sgrPaletteNoncePrefix, sgrPaletteSuiteId,
    gradeSgrPaletteCase, sgrPaletteCellAt, sgrPaletteObservation, validateSgrPaletteScreen,
    pinnedNamedNotExposedNote, pinnedNamedIndistinguishableNote, pinnedDefaultNotExposedNote, pinnedDefaultSetupNote,
    pinnedIndexedNotExposedNote, pinnedIndexedUncalibratedNote, cubeReferences } from './sgr-palette-contracts.mjs';
import { colorScreenOf, validateColorEvidence, buildColorReport, sameRgb, sameCellColor, incomplete, decided } from './sgr-color-grading-core.mjs';
import { sgrPaletteAdapterId } from './sgr-palette-grading-collector.mjs';
import { sgrPaletteRecordingContext } from './sgr-palette-collector.mjs';

/** The suite's report spec: identities, pinned replay and scope. */
const spec = Object.freeze({ label: 'SGR-palette', prefix: 'sgpal', noncePrefix: sgrPaletteNoncePrefix,
    contracts: sgrPaletteContracts, caseIds: sgrPaletteCaseIds, suiteId: sgrPaletteSuiteId, version: sgrPaletteContractsVersion,
    adapterId: sgrPaletteAdapterId, derivedFrom: 'ntilde-owned-sgr-attributes-grading-v1',
    screenOf: colorScreenOf(validateSgrPaletteScreen), observation: sgrPaletteObservation, grade: gradeSgrPaletteCase,
    replayPinned: (contract, screens) => replaySgrPalettePinned(contract, screens),
    recordingContext: sgrPaletteRecordingContext, replayFn: 'ntildeRunSgrPaletteTermless',
    scope: 'Pinned terminfo.dev@4.0.0 bright, 256-color and default-color termless criteria replayed byte-exactly on a disposable App '
            + 'and graded from parent-journaled readScreen stored palette indices, default markers and literal bytes together with '
            + 'App-resolved RGB bound to one recorded palette/theme state, with controls for the unaffected channel, unrelated '
            + 'attributes, DECSCNM, bold-versus-bright, boundary indices and reset restoration. '
            + 'Rendered glyph appearance is not claimed; no stock grades.' });

/** Validate journal, run identity, binding stability and restoration. */
export const validateSgrPaletteEvidence = (frames, run, runId, adapter) => validateColorEvidence(spec, frames, run, runId, adapter);

/** Re-derive the pinned namedColorProbe termless result over its three cells. */
function replayNamed(contract, cells) {
    const [baseline, first, second] = cells;
    const response = JSON.stringify({ baseline, first, second });
    if (baseline.char !== 'C' || first.char !== 'X' || second.char !== 'Y') return incomplete(response, pinnedNamedNotExposedNote);
    const neutral = baseline[contract.channel], a = first[contract.channel], b = second[contract.channel];
    if (neutral !== undefined && a && b && !sameCellColor(neutral, a) && !sameRgb(a, b)) return decided(true, contract, response);
    return incomplete(response, pinnedNamedIndistinguishableNote);
}

/** Re-derive the pinned defaultColorProbe termless result over its three cells. */
function replayDefault(contract, cells) {
    const [baseline, before, after] = cells;
    const response = JSON.stringify({ baseline, before, after });
    if (baseline.char !== 'C' || before.char !== 'X' || after.char !== 'R') return incomplete(response, pinnedDefaultNotExposedNote);
    const baseColor = baseline[contract.channel], changed = before[contract.channel], resetColor = after[contract.channel];
    if (!changed || sameCellColor(baseColor, changed) || resetColor === undefined) return incomplete(response, pinnedDefaultSetupNote);
    return decided(sameCellColor(baseColor, resetColor), contract, response);
}

/** Re-derive the pinned indexedColorProbe termless result over its five cells. */
function replayIndexed(contract, cells) {
    const [baseline, directFirst, directSecond, indexedFirst, indexedSecond] = cells;
    const response = JSON.stringify({ baseline, directFirst, directSecond, indexedFirst, indexedSecond });
    if (baseline.char !== 'C' || directFirst.char !== 'A' || directSecond.char !== 'B'
        || indexedFirst.char !== 'X' || indexedSecond.char !== 'Y') return incomplete(response, pinnedIndexedNotExposedNote);
    const [first, second] = cubeReferences;
    const a = directFirst[contract.channel], b = directSecond[contract.channel];
    const x = indexedFirst[contract.channel], y = indexedSecond[contract.channel];
    if (a && b && x && y && sameRgb(a, first.rgb) && sameRgb(b, second.rgb) && sameRgb(x, a) && sameRgb(y, b)) {
        return decided(true, contract, response);
    }
    return incomplete(response, pinnedIndexedUncalibratedNote);
}

const replays = Object.freeze({ named: replayNamed, default: replayDefault, indexed: replayIndexed });

/** Re-derive the complete pinned termless result from the observed screen of the
 * pinned fixture, mirroring the pinned code's shapes exactly. */
export function replaySgrPalettePinned(contract, screens) {
    const replay = replays[contract.pinnedKind];
    if (!replay) throw new Error(`Unknown pinned kind for ${contract.caseId}.`);
    const cells = Array.from({ length: contract.fixtureCells }, (_, col) => sgrPaletteCellAt(screens[contract.pinnedRead], 0, col));
    return replay(contract, cells);
}


/** Re-derive and cross-check every case; throw on any disagreement. */
export const buildSgrPaletteGradingReport = (frames, run, runId, adapter, options) => buildColorReport(spec, frames, run, runId, adapter, options);
