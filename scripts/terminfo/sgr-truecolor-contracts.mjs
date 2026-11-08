// Versioned probe contracts for the SGR truecolor qualification batch:
// sgr.fg.truecolor and sgr.bg.truecolor.
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6). The IDs
// match the pinned collector exactly; no related variant is substituted:
//   truecolorProbe("sgr.fg.truecolor", "fg")  feed `C ESC[31m A ESC[34m B ESC[38;2;255;128;0m X ESC[38;2;17;97;201m Y`
//   truecolorProbe("sgr.bg.truecolor", "bg")  feed `C ESC[41m A ESC[44m B ESC[48;2;0;255;128m X ESC[48;2;17;97;201m Y`
// NAMING: the pinned bundle also registers `extensions.truecolor` (foreground
// only, a different group and criterion). It is a related variant, NOT part of
// this batch, and is never graded or substituted here.
// SYNTAX: only the semicolon form 38/48;2;r;g;b exercised by the pinned fixture
// is qualified. Colon forms, optional colorspace ids and combined single-sequence
// forms are separate variants and are neither fed nor claimed.
// The pinned termless probe reads App-exposed per-cell colors, where a default
// channel is null and a palette or literal channel is the resolved {r,g,b}. It
// grades supported, unsupported (the requested values differ while the two
// ordinary-color controls are distinguishable) or inconclusive.
//
// This contract grader keeps three layers distinct and records the first two
// beside the verdict:
//   stored   - literal ARGB bytes per target cell, with the palette index and
//              the default marker proven cleared;
//   resolved - the App's storage-to-theme RGB bound to ONE recorded palette and
//              theme (resolved-colors.mjs; resolution lives in App/Rendering,
//              never in VT);
//   rendered - NOT claimed: no pinned criterion requires glyph appearance.
// Unsupported needs valid controls and an observed mismatch; missing cells,
// invalid controls, DECSCNM, or indistinguishable/uncalibrated controls are
// inconclusive and never unsupported.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsCellAt, sgrAttrsReset, sgrAttrsClear, validateSgrAttrsScreen } from './sgr-attrs-contracts.mjs';
import { expandResolvedColors } from './resolved-colors.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const sgrTruecolorContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const sgrTruecolorSuiteId = 'ntilde-owned-sgr-truecolor-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const sgrTruecolorNoncePrefix = 'NTILDE_ST_';
/** The measured reset every plan begins with (DECSCNM, DECOM, margins, IRM, SGR off, clear, home). */
export const sgrTruecolorReset = sgrAttrsReset;
/** Between the pinned fixture and the control cells: SGR off, clear, home. */
export const sgrTruecolorClear = sgrAttrsClear;

/** One projection of one cell: the attribute suite's, which already carries flags,
 * stored details (indices, literal ARGB, underline color, overline) and resolved RGB. */
export function sgrTruecolorCellAt(screen, row, col) {
    const cell = sgrAttrsCellAt(screen, row, col);
    // The stored indices come from the cell details, the flags from the attribute runs and the resolved
    // channels from the resolved runs: all three must name the same palette index for this one cell.
    const position = row * screen.cols + col;
    let offset = 0, run;
    for (const candidate of screen.attributeRuns ?? []) {
        if (position < offset + candidate.count) { run = candidate; break; }
        offset += candidate.count;
    }
    const resolved = expandResolvedColors(screen.resolvedColors, screen.rows * screen.cols, screen.colorBinding)[position];
    for (const [channel, field] of [['fg', 'fg'], ['bg', 'bg']]) {
        const resolvedIndex = resolved[channel].source === 'palette' ? resolved[channel].index : -1;
        if (run?.[field] !== cell[channel + 'Index'] || resolvedIndex !== cell[channel + 'Index']) {
            throw new Error(`Contradictory stored and resolved ${channel} palette indices at ${row},${col}.`);
        }
    }
    return cell;
}
/** Observation screens are validated exactly like the attribute suite's. */
export const validateSgrTruecolorScreen = validateSgrAttrsScreen;

/** The pinned note texts, byte for byte. */
export const pinnedTruecolorNotExposedNote = 'RGB target or independent color-control cells were not exposed';
export const pinnedTruecolorReadbackNote = 'RGB target readback was absent';
export const pinnedTruecolorMimicNote = 'The preceding color can mimic an ignored first RGB request';
export const pinnedTruecolorCalibrationNote = 'Independent color-channel calibration was absent';

const attributeNames = Object.freeze(['bold', 'dim', 'italic', 'underline', 'inverse', 'hidden', 'strikethrough', 'blink', 'overline']);
const literalField = channel => (channel === 'fg' ? 'foregroundArgb' : 'backgroundArgb');

/** No style attribute (other than `except`), flag bit, underline style or underline color is set. */
const attributesClear = (cell, except = []) => attributeNames.every(name => except.includes(name)
        || (name === 'underline' ? cell.underline === false : cell[name] === false))
    && (except.length > 0 || (cell.flags & 0xff) === 0)
    && cell.underlineStyle === 0 && cell.underlineColorIndex === -1 && cell.underlineArgb === null;
/** The channel is on the default marker: default source, no index, no literal bytes. */
export const isDefault = (cell, channel) => cell[channel + 'Source'] === 'default' && cell[channel + 'Index'] === -1
    && cell[literalField(channel)] === null;
/** The channel stores exactly this palette index and no literal bytes. */
export const isPalette = (cell, channel, index) => cell[channel + 'Source'] === 'palette' && cell[channel + 'Index'] === index
    && cell[literalField(channel)] === null;
/** The channel stores exactly these literal RGB bytes (alpha-agnostic) and no palette index. */
export const isLiteral = (cell, channel, rgb) => cell[channel + 'Source'] === 'literal' && cell[channel + 'Index'] === -1
    && cell[literalField(channel)] !== null
    && (cell[literalField(channel)] & 0xffffff) === ((rgb.r << 16) | (rgb.g << 8) | rgb.b);
/** A cell with no attribute and both channels on default markers. */
const neutral = cell => attributesClear(cell) && isDefault(cell, 'fg') && isDefault(cell, 'bg');

const sameRgb = (a, b) => a.r === b.r && a.g === b.g && a.b === b.b;
const sameCellColor = (a, b) => {
    if (a === undefined || b === undefined) return false;
    if (a === null || b === null) return a === b;
    return sameRgb(a, b);
};
/** Every verdict carries both layer states; layers a case never reached are `not-evaluated`. */
const inconclusive = (note, layers = { stored: 'not-evaluated', resolved: 'not-evaluated' }) => ({ outcome: 'inconclusive', note, layers });
const argbRgb = argb => ({ r: (argb >> 16) & 255, g: (argb >> 8) & 255, b: argb & 255 });

/** Case builder: pinned fixture, then a control group on a cleared screen. */
const case_ = (fields) => Object.freeze({
    pinnedPath: 'termless', pinnedRead: 0, fixtureCells: fields.fixtureCells,
    pinnedFeeds: Object.freeze([fields.fixture]),
    plan: Object.freeze([planFeed(sgrTruecolorReset), planFeed(fields.fixture), planRead('cells'),
        planFeed(sgrTruecolorClear), planFeed(fields.control), planRead('cells')]),
    ...fields,
});

/** One pinned truecolor case: ordinary-color controls A/B, then two literal targets X/Y. */
function truecolorCase(caseId, channel, sgr, ordinaryCodes, ordinaryIndices, firstRgb, otherCode, otherIndex) {
    const other = channel === 'fg' ? 'bg' : 'fg';
    const first = Object.freeze({ ...firstRgb });
    const second = Object.freeze({ r: 17, g: 97, b: 201 });
    const sample = Object.freeze({ index: 67, rgb: Object.freeze({ r: 95, g: 135, b: 175 }) });
    return case_({
        caseId, probeId: caseId, pinnedKind: 'truecolor', channel, other, first, second,
        pinnedExpected: `${caseId}: two distinct direct-RGB samples are exposed at their exact requested values`,
        pinnedCriterion: 'two distinct direct-RGB samples are exposed at their exact requested values',
        fixture: `C\x1b[${ordinaryCodes[0]}mA\x1b[${ordinaryCodes[1]}mB\x1b[${sgr};2;${first.r};${first.g};${first.b}mX\x1b[${sgr};2;17;97;201mY`,
        fixtureCells: 5, controlCells: 6,
        // Literal and palette storage with the SAME resolved appearance (95,135,175 vs index 67), boundary
        // components, and an unaffected explicit palette channel; then SGR 0 must leave neutral.
        control: `\x1b[${sgr};2;95;135;175mP\x1b[${sgr};5;67mQ\x1b[${sgr};2;0;0;0mL\x1b[${sgr};2;255;255;255mH`
            + `\x1b[0m\x1b[${otherCode}m\x1b[${sgr};2;17;97;201mU\x1b[0mR`,
        terminalFixture: { writes: [`\x1b[${sgr};2;${first.r};${first.g};${first.b}m`], demand: 'consumed (cursor advance does not verify SGR colors)', minimum: { rows: 1, cols: 2 } },
        appearanceScope: `stored literal ARGB bytes ${first.r},${first.g},${first.b} and 17,97,201 with the palette index and default marker cleared, `
            + `resolved ${channel} RGB equal to the requested values under the one recorded palette; the semicolon 38/48;2;r;g;b form only; `
            + 'rendered appearance is not claimed',
        grade(observation) {
            const { fixture, control, reverseVideo } = observation;
            const [baseline, ordinaryFirst, ordinarySecond, targetFirst, targetSecond] = fixture;
            if (baseline.char !== 'C' || ordinaryFirst.char !== 'A' || ordinarySecond.char !== 'B'
                || targetFirst.char !== 'X' || targetSecond.char !== 'Y') return inconclusive(pinnedTruecolorNotExposedNote);
            if (reverseVideo !== false) return inconclusive('DECSCNM reverse video was not proven off, so stored colors cannot be separated from it');
            if (!neutral(baseline)) return inconclusive('Neutral baseline control was not measured (a color or attribute was initially active)');
            const x = targetFirst[channel], y = targetSecond[channel];
            // A null channel is the stored default marker (an observed state judged below); undefined is missing readback.
            if (x === undefined || y === undefined) return inconclusive(pinnedTruecolorReadbackNote);
            // The independent ordinary-color controls must be the contracted standard palette cells.
            if (!isPalette(ordinaryFirst, channel, ordinaryIndices[0]) || !isPalette(ordinarySecond, channel, ordinaryIndices[1])
                || ![ordinaryFirst, ordinarySecond, targetFirst, targetSecond].every(cell => isDefault(cell, other) && attributesClear(cell))) {
                return inconclusive('The ordinary-color controls or unaffected channel or unrelated attributes were not measured as contracted');
            }
            const [reference, indexed, low, high, isolated, reset] = control;
            if (reference.char !== 'P' || indexed.char !== 'Q' || low.char !== 'L' || high.char !== 'H'
                || isolated.char !== 'U' || reset.char !== 'R'
                || !isLiteral(reference, channel, sample.rgb) || !isPalette(indexed, channel, sample.index)
                || !isLiteral(low, channel, { r: 0, g: 0, b: 0 }) || !isLiteral(high, channel, { r: 255, g: 255, b: 255 })
                || ![reference, indexed, low, high].every(cell => isDefault(cell, other) && attributesClear(cell))
                || !isLiteral(isolated, channel, second) || !isPalette(isolated, other, otherIndex) || !attributesClear(isolated)
                || !neutral(reset)) {
                return inconclusive('Literal-versus-palette, boundary-component or unaffected-channel control cells were not measured as contracted');
            }
            // Equal resolved appearance must not conceal storage: P (literal) and Q (index 67) resolve alike here.
            if (!reference[channel] || !indexed[channel] || !sameRgb(reference[channel], indexed[channel])) {
                return inconclusive('The literal and palette controls do not resolve alike under the recorded palette, so storage cannot be separated from appearance');
            }
            const preceding = ordinarySecond[channel], a = ordinaryFirst[channel];
            if (!preceding || sameRgb(preceding, first)) return inconclusive(pinnedTruecolorMimicNote);
            if (!a || sameRgb(a, preceding)) return inconclusive(pinnedTruecolorCalibrationNote);
            const storedOk = isLiteral(targetFirst, channel, first) && isLiteral(targetSecond, channel, second);
            if (!storedOk) {
                return { outcome: 'unsupported', layers: { stored: 'unsupported', resolved: 'not-evaluated' },
                    note: `SGR ${sgr};2 did not store the requested literal RGB bytes` };
            }
            if (sameRgb(x, first) && sameRgb(y, second)) {
                return { outcome: 'supported', layers: { stored: 'supported', resolved: 'supported' } };
            }
            return { outcome: 'unsupported', layers: { stored: 'supported', resolved: 'unsupported' },
                note: `The stored literal ${channel} bytes did not resolve to the requested RGB values` };
        },
        observed(observation) {
            return briefObservation(observation, ['fgSource', 'fgIndex', 'foregroundArgb', 'bgSource', 'bgIndex', 'backgroundArgb', 'fg', 'bg']);
        },
    });
}

/** The two pinned cases, in the qualification's order. */
export const sgrTruecolorContracts = Object.freeze([
    truecolorCase('sgr.fg.truecolor', 'fg', 38, [31, 34], [1, 4], { r: 255, g: 128, b: 0 }, 42, 2),
    truecolorCase('sgr.bg.truecolor', 'bg', 48, [41, 44], [1, 4], { r: 0, g: 255, b: 128 }, 31, 1),
]);

/** A compact observed record: stored text, flags and the named fields per cell. */
function briefObservation(observation, fields) {
    const brief = cell => ({ char: cell.char, flags: cell.flags, ...Object.fromEntries(fields.map(name => [name, cell[name]])) });
    return { fixture: observation.fixture.map(brief), control: observation.control.map(brief),
        reverseVideo: observation.reverseVideo, defaults: observation.defaults };
}

/** Contract case ids in suite order. */
export const sgrTruecolorCaseIds = Object.freeze(sgrTruecolorContracts.map(contract => contract.caseId));

/** Project the two read groups of one case into the grader's observation. */
export function sgrTruecolorObservation(contract, screens) {
    if (!Array.isArray(screens) || screens.length !== 2) throw new Error('Missing SGR-truecolor read captures.');
    const binding = screens.map(screen => screen.colorBinding.reverseVideo);
    const theme = screens[0].colorBinding;
    return { fixture: Array.from({ length: contract.fixtureCells }, (_, col) => sgrTruecolorCellAt(screens[0], 0, col)),
        control: Array.from({ length: contract.controlCells }, (_, col) => sgrTruecolorCellAt(screens[1], 0, col)),
        reverseVideo: binding.every(value => value === false) ? false : binding.some(value => value === true) ? true : null,
        defaults: { fg: argbRgb(theme.foregroundArgb), bg: argbRgb(theme.backgroundArgb) } };
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeSgrTruecolorCase(contract, observation, revision = sgrTruecolorContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown SGR-truecolor contracts revision ${revision}.`);
    if (!Array.isArray(observation?.fixture) || observation.fixture.length !== contract.fixtureCells
        || !Array.isArray(observation.control) || observation.control.length !== contract.controlCells || !observation.defaults) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as sgrTruecolorFeeds, planReads as sgrTruecolorReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateSgrTruecolorFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}
