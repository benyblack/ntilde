// Versioned probe contracts for the SGR palette-color qualification batch:
// sgr.fg.bright, sgr.bg.bright, sgr.fg.256, sgr.bg.256, sgr.fg.default and
// sgr.bg.default.
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6). The IDs
// match the pinned collector exactly; no related variant is substituted:
//   namedColorProbe("sgr.fg.bright", "fg", 91, 94)   feed `C ESC[91m X ESC[94m Y`
//   namedColorProbe("sgr.bg.bright", "bg", 101, 104) feed `C ESC[101m X ESC[104m Y`
//   defaultColorProbe("sgr.fg.default", "fg")        feed `C ESC[31m X ESC[39m R`
//   defaultColorProbe("sgr.bg.default", "bg")        feed `C ESC[42m X ESC[49m R`
//   indexedColorProbe("sgr.fg.256", "fg")            feed `C ESC[38;2;95;135;175m A ESC[38;2;215;135;95m B ESC[38;5;67m X ESC[38;5;173m Y`
//   indexedColorProbe("sgr.bg.256", "bg")            same with 48
// Each termless probe reads App-exposed per-cell colors, where a default
// channel is null and a palette or literal channel is the resolved {r,g,b}.
// Unlike the attribute probes, these probes may grade unsupported (the default
// probe does when the reset color differs from the baseline); the named and
// indexed probes grade supported or inconclusive only.
//
// This contract grader keeps three layers distinct and records the first two
// beside the verdict:
//   stored   - palette index / default marker / literal ARGB storage per cell;
//   resolved - the App's storage-to-theme RGB bound to ONE recorded palette and
//              theme (resolved-colors.mjs; resolution lives in App/Rendering,
//              never in VT);
//   rendered - NOT claimed: no pinned criterion requires glyph appearance.
// Unsupported needs valid controls and an observed stored mismatch; missing
// cells, invalid controls, DECSCNM, or indistinguishable/uncalibrated resolved
// colors are inconclusive and never unsupported.
import { validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsReset, sgrAttrsClear, validateSgrAttrsScreen } from './sgr-attrs-contracts.mjs';
import { sgrColorCellAt, attributesClear, isDefault, isPalette, isLiteral, neutral, sameRgb, sameCellColor, inconclusive,
    case_, briefObservation, colorObservation, gradeColorCase } from './sgr-color-core.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const sgrPaletteContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const sgrPaletteSuiteId = 'ntilde-owned-sgr-palette-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const sgrPaletteNoncePrefix = 'NTILDE_SP_';
/** The measured reset every plan begins with (DECSCNM, DECOM, margins, IRM, SGR off, clear, home). */
export const sgrPaletteReset = sgrAttrsReset;
/** Between the pinned fixture and the control cells: SGR off, clear, home. */
export const sgrPaletteClear = sgrAttrsClear;

/** One projection of one cell: the attribute suite's, which already carries flags,
 * stored details (indices, literal ARGB, underline color, overline) and resolved RGB. */
export const sgrPaletteCellAt = sgrColorCellAt;
/** Observation screens are validated exactly like the attribute suite's. */
export const validateSgrPaletteScreen = validateSgrAttrsScreen;

/** The pinned note texts, byte for byte. */
export const pinnedNamedNotExposedNote = 'Neutral control or named-color cells were not exposed';
export const pinnedNamedIndistinguishableNote = 'Baseline, theme colors, or color readback were indistinguishable';
export const pinnedDefaultNotExposedNote = 'Baseline, colored, or reset cell was not exposed';
export const pinnedDefaultSetupNote = 'Distinct colored setup or reset color readback was absent';
export const pinnedIndexedNotExposedNote = 'Indexed or direct-RGB control cells were not exposed';
export const pinnedIndexedUncalibratedNote = 'RGB reference was not calibrated, or the backend may use a custom indexed palette';

/** The two canonical xterm 256-cube references of the pinned indexed probe (indices 67 and 173). */
export const cubeReferences = Object.freeze([
    Object.freeze({ index: 67, rgb: Object.freeze({ r: 95, g: 135, b: 175 }) }),
    Object.freeze({ index: 173, rgb: Object.freeze({ r: 215, g: 135, b: 95 }) }),
]);

export { isDefault, isPalette, isLiteral };

/** sgr.fg.bright / sgr.bg.bright: exact bright palette indices (8+n), bold stays separate. */
function namedCase(caseId, channel, codes, indices, standardIndex, boldSetup) {
    const other = channel === 'fg' ? 'bg' : 'fg';
    const [firstCode, secondCode] = codes;
    return case_({
        caseId, probeId: caseId, pinnedKind: 'named', channel, other,
        pinnedExpected: `${caseId}: the two named colors select distinct exposed ${channel} RGB values`,
        pinnedCriterion: `the two named colors select distinct exposed ${channel} RGB values`,
        fixture: `C\x1b[${firstCode}mX\x1b[${secondCode}mY`, fixtureCells: 3,
        control: `${boldSetup}P\x1b[0m\x1b[${firstCode}mQ\x1b[0mR`,
        terminalFixture: { writes: [`\x1b[${firstCode}mX`], demand: 'consumed (cursor advance does not verify SGR colors)', minimum: { rows: 1, cols: 2 } },
        appearanceScope: `stored bright palette indices ${indices.join(' and ')} (not bold, not the standard indices), `
            + `resolved ${channel} RGB at the theme-resolution stage of the one recorded palette; rendered appearance is not claimed`,
        grade(observation) {
            const { fixture, control, reverseVideo } = observation;
            const [baseline, first, second] = fixture;
            if (baseline.char !== 'C' || first.char !== 'X' || second.char !== 'Y') return inconclusive(pinnedNamedNotExposedNote);
            if (reverseVideo !== false) return inconclusive('DECSCNM reverse video was not proven off, so stored colors cannot be separated from it');
            if (!neutral(baseline)) return inconclusive('Neutral baseline control was not measured (a color or attribute was initially active)');
            // Bold is allowed to move on the targets: bright-by-bold would be the confusion this case rejects.
            if (![first, second].every(cell => isDefault(cell, other) && attributesClear(cell, ['bold']))) {
                return inconclusive('The unaffected channel or unrelated attributes drifted on the target cells, so the setup control is invalid');
            }
            const [bold, repeat, reset] = control;
            // Bold + the standard color must store the STANDARD index with the bold flag, a repeat of the
            // target code after SGR 0 must store exactly what the target stored (color and bold), and SGR 0
            // must leave neutral. The repeat is a consistency control only: it never asserts the target's indices.
            if (bold.char !== 'P' || repeat.char !== 'Q' || reset.char !== 'R'
                || bold.bold !== true || !attributesClear(bold, ['bold']) || !isPalette(bold, channel, standardIndex) || !isDefault(bold, other)
                || repeat.bold !== first.bold || !attributesClear(repeat, ['bold']) || !isDefault(repeat, other)
                || repeat[channel + 'Source'] !== first[channel + 'Source'] || repeat[channel + 'Index'] !== first[channel + 'Index']
                || !neutral(reset)) {
                return inconclusive('Bold-versus-bright or reset control cells were not measured as contracted');
            }
            const stored = isPalette(first, channel, indices[0]) && isPalette(second, channel, indices[1])
                && first.bold === false && second.bold === false;
            if (!stored) {
                const layers = { stored: 'unsupported', resolved: 'not-evaluated' };
                return { outcome: 'unsupported', layers, note: first.bold || second.bold
                    ? 'Bright was selected through bold/intensity instead of the bright palette indices'
                    : `SGR ${firstCode}/${secondCode} did not store palette indices ${indices.join('/')}` };
            }
            const a = first[channel], b = second[channel];
            if (baseline[channel] !== undefined && a && b && !sameCellColor(baseline[channel], a) && !sameRgb(a, b)) {
                return { outcome: 'supported', layers: { stored: 'supported', resolved: 'supported' } };
            }
            return inconclusive(pinnedNamedIndistinguishableNote, { stored: 'supported', resolved: 'inconclusive' });
        },
        observed(observation) { return briefObservation(observation, ['fgSource', 'fgIndex', 'bgSource', 'bgIndex', 'fg', 'bg', 'bold']); },
    });
}

/** sgr.fg.default / sgr.bg.default: explicit nondefault setup, then the default marker returns. */
function defaultCase(caseId, channel, setupCode, resetCode, setupIndex) {
    const other = channel === 'fg' ? 'bg' : 'fg';
    return case_({
        caseId, probeId: caseId, pinnedKind: 'default', channel, other,
        pinnedExpected: `${caseId}: reset R restores the measured C ${channel} after a distinct colored X`,
        pinnedCriterion: `reset R restores the measured C ${channel} after a distinct colored X`,
        fixture: `C\x1b[${setupCode}mX\x1b[${resetCode}mR`, fixtureCells: 3,
        // Both channels explicit, then only this channel is reset: the unaffected channel must survive.
        control: `\x1b[31;42mA\x1b[${resetCode}mB\x1b[0mR`,
        terminalFixture: { writes: [`\x1b[${resetCode}m`], demand: 'consumed (cursor advance does not verify SGR colors)', minimum: { rows: 1, cols: 2 } },
        appearanceScope: `explicit stored palette ${setupIndex} setup, then the stored ${channel} default marker with no palette or literal bytes; `
            + `the setup must resolve distinctly from the theme default; rendered appearance is not claimed`,
        grade(observation) {
            const { fixture, control, reverseVideo, defaults } = observation;
            const [baseline, setup, reset] = fixture;
            if (baseline.char !== 'C' || setup.char !== 'X' || reset.char !== 'R') return inconclusive(pinnedDefaultNotExposedNote);
            if (reverseVideo !== false) return inconclusive('DECSCNM reverse video was not proven off, so default colors cannot be separated from it');
            if (!neutral(baseline)) return inconclusive('Neutral baseline control was not measured (a color or attribute was initially active)');
            // The reset only means something after an explicit, distinguishable nondefault setup.
            if (!isPalette(setup, channel, setupIndex) || !isDefault(setup, other) || !attributesClear(setup)) {
                return inconclusive(pinnedDefaultSetupNote, { stored: 'inconclusive', resolved: 'not-evaluated' });
            }
            if (!setup[channel] || sameRgb(setup[channel], defaults[channel])) {
                return inconclusive('The explicit setup resolves to the theme default, so the reset cannot be distinguished from it', { stored: 'supported', resolved: 'inconclusive' });
            }
            const [both, partial, restored] = control;
            if (both.char !== 'A' || partial.char !== 'B' || restored.char !== 'R'
                || !isPalette(both, 'fg', 1) || !isPalette(both, 'bg', 2) || !attributesClear(both)
                || !attributesClear(partial) || !neutral(restored)) {
                return inconclusive('Unaffected-channel control cells were not measured as contracted');
            }
            const layers = { stored: 'unsupported', resolved: 'not-evaluated' };
            if (!isDefault(partial, channel) || !isPalette(partial, other, other === 'fg' ? 1 : 2)) {
                return { outcome: 'unsupported', layers, note: `SGR ${resetCode} did not reset only the ${channel} channel` };
            }
            // Attribute drift on the reset cell is an invalid control, not a failed color reset.
            if (!attributesClear(reset)) return inconclusive('Unrelated attributes drifted on the reset cell');
            if (!isDefault(reset, channel) || !isDefault(reset, other)) {
                return { outcome: 'unsupported', layers, note: `SGR ${resetCode} did not restore the ${channel} default marker after the explicit setup` };
            }
            return { outcome: 'supported', layers: { stored: 'supported', resolved: 'supported' } };
        },
        observed(observation) { return briefObservation(observation, ['fgSource', 'fgIndex', 'bgSource', 'bgIndex', 'fg', 'bg']); },
    });
}

/** sgr.fg.256 / sgr.bg.256: exact palette indices 67 and 173 beside literal RGB references. */
function indexedCase(caseId, channel, sgr) {
    const other = channel === 'fg' ? 'bg' : 'fg';
    const [first, second] = cubeReferences;
    return case_({
        caseId, probeId: caseId, pinnedKind: 'indexed', channel, other,
        pinnedExpected: `${caseId}: indices 67 and 173 match two independently observed canonical RGB cube references`,
        pinnedCriterion: 'indices 67 and 173 match two independently observed canonical RGB cube references',
        fixture: `C\x1b[${sgr};2;95;135;175mA\x1b[${sgr};2;215;135;95mB\x1b[${sgr};5;67mX\x1b[${sgr};5;173mY`, fixtureCells: 5,
        // Index 0 and 255 are palette storage, never the default marker.
        control: `\x1b[${sgr};5;0mP\x1b[${sgr};5;255mQ\x1b[0mR`,
        terminalFixture: { writes: [`\x1b[${sgr};5;67m`], demand: 'consumed (cursor advance does not verify SGR colors)', minimum: { rows: 1, cols: 2 } },
        appearanceScope: `stored palette indices 67 and 173 distinct from the literal RGB reference cells, resolved ${channel} RGB equal to the `
            + 'canonical cube references under the one recorded palette; rendered appearance is not claimed',
        grade(observation) {
            const { fixture, control, reverseVideo } = observation;
            const [baseline, directFirst, directSecond, indexedFirst, indexedSecond] = fixture;
            if (baseline.char !== 'C' || directFirst.char !== 'A' || directSecond.char !== 'B'
                || indexedFirst.char !== 'X' || indexedSecond.char !== 'Y') return inconclusive(pinnedIndexedNotExposedNote);
            if (reverseVideo !== false) return inconclusive('DECSCNM reverse video was not proven off, so stored colors cannot be separated from it');
            if (!neutral(baseline)) return inconclusive('Neutral baseline control was not measured (a color or attribute was initially active)');
            if (![directFirst, directSecond, indexedFirst, indexedSecond].every(cell => isDefault(cell, other) && attributesClear(cell))) {
                return inconclusive('The unaffected channel or unrelated attributes drifted on the target cells, so the setup control is invalid');
            }
            // The direct-RGB references must be stored literally, never as palette indices.
            if (!isLiteral(directFirst, channel, first.rgb) || !isLiteral(directSecond, channel, second.rgb)) {
                return inconclusive('The direct-RGB reference cells were not stored as the requested literal colors', { stored: 'inconclusive', resolved: 'not-evaluated' });
            }
            const [zero, top, reset] = control;
            if (zero.char !== 'P' || top.char !== 'Q' || reset.char !== 'R'
                || !isPalette(zero, channel, 0) || !isPalette(top, channel, 255)
                || !isDefault(zero, other) || !isDefault(top, other)
                || !attributesClear(zero) || !attributesClear(top) || !neutral(reset)) {
                return inconclusive('Palette boundary-index control cells were not measured as contracted');
            }
            if (!isPalette(indexedFirst, channel, first.index) || !isPalette(indexedSecond, channel, second.index)) {
                return { outcome: 'unsupported', layers: { stored: 'unsupported', resolved: 'not-evaluated' },
                    note: `SGR ${sgr};5 did not store palette indices ${first.index}/${second.index}` };
            }
            const a = directFirst[channel], b = directSecond[channel], x = indexedFirst[channel], y = indexedSecond[channel];
            if (a && b && x && y && sameRgb(a, first.rgb) && sameRgb(b, second.rgb) && sameRgb(x, a) && sameRgb(y, b)) {
                return { outcome: 'supported', layers: { stored: 'supported', resolved: 'supported' } };
            }
            return inconclusive(pinnedIndexedUncalibratedNote, { stored: 'supported', resolved: 'inconclusive' });
        },
        observed(observation) {
            return briefObservation(observation, ['fgSource', 'fgIndex', 'foregroundArgb', 'bgSource', 'bgIndex', 'backgroundArgb', 'fg', 'bg']);
        },
    });
}

/** The six pinned cases, in the qualification's order. */
export const sgrPaletteContracts = Object.freeze([
    namedCase('sgr.fg.bright', 'fg', [91, 94], [9, 12], 1, '\x1b[1;31m'),
    namedCase('sgr.bg.bright', 'bg', [101, 104], [9, 12], 1, '\x1b[1;41m'),
    indexedCase('sgr.fg.256', 'fg', 38),
    indexedCase('sgr.bg.256', 'bg', 48),
    defaultCase('sgr.fg.default', 'fg', 31, 39, 1),
    defaultCase('sgr.bg.default', 'bg', 42, 49, 2),
]);

/** Contract case ids in suite order. */
export const sgrPaletteCaseIds = Object.freeze(sgrPaletteContracts.map(contract => contract.caseId));

/** Observation of one case's two read groups. */
export const sgrPaletteObservation = (contract, screens) => colorObservation(contract, screens, 'SGR-palette');

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export const gradeSgrPaletteCase = (contract, observation, revision = sgrPaletteContractsVersion) =>
    gradeColorCase(contract, observation, revision, 'SGR-palette');

export { planFeeds as sgrPaletteFeeds, planReads as sgrPaletteReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateSgrPaletteFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}
