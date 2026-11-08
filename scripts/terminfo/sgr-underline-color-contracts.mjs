// Versioned probe contracts for the SGR underline-color qualification batch:
// sgr.underline-color-indexed, sgr.underline-color-rgb and sgr.underline-color-reset.
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6), termless path:
//   indexed  feed ESC[4;38;5;4m ESC[59m A ESC[58;5;5m X ESC[38;5;5m ESC[59m B ESC[58;5;4m Y
//            read A,X,B,Y: supported when X's underline color equals B's resolved foreground
//            (palette 5) and Y's equals A's (palette 4), with distinct palette controls.
//   rgb      feed ESC[4m ESC[59m ESC[38;2;0;0;255m A ESC[38;2;0;255;0m B ESC[58;2;255;0;128m X
//            read A,B,X: supported when X's underline color is rgb(255,0,128) and B's default
//            underline is not that color.
//   reset    feed ESC[4m ESC[59m C ESC[58;2;255;0;128m X ESC[59m Y
//            read C,X,Y: X is rgb(255,0,128); SGR 59 returns Y to C's measured default.
// The registration `sgr.underline.color` (the aggregate case) shares the RGB probe body
// under a different id; it is NOT part of this batch and is neither graded nor substituted.
// The pinned probes never return unsupported from an unexposed default; a negative needs
// calibrated underline-color readback. This contract grader may grade unsupported, but only
// after valid controls establish an observed mismatch.
//
// Three layers stay distinct. STORED: the underline style, the explicit underline color as
// an index or literal ARGB bytes, or the default marker (no explicit color). RESOLVED: the
// RGB the pinned cell shape reports. It is derived parent-side, never reported by the App:
// a palette index resolves through the recorded palette binding (the draw path's
// ResolveUnderlineColor uses the same palette entry as a foreground index), a literal is its
// bytes, and a default underline reports null because it carries no color of its own — it
// inherits the cell's text color at draw time, so it is not a fixed RGB. RENDERED
// appearance is never claimed.
import { planFeed, planRead, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsReset, sgrAttrsClear, validateSgrAttrsScreen } from './sgr-attrs-contracts.mjs';
import { sgrColorCellAt, isDefault, isPalette, isLiteral, argbRgb, sameRgb } from './sgr-color-core.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const sgrUnderlineColorContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const sgrUnderlineColorSuiteId = 'ntilde-owned-sgr-underline-color-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const sgrUnderlineColorNoncePrefix = 'NTILDE_UC_';
/** The measured reset every plan begins with: the attribute reset plus OSC 104 (all palette overrides cleared). */
export const sgrUnderlineColorReset = sgrAttrsReset + '\x1b]104\x07';
/** The attribute suite's screen validation: attribute runs, stored details and resolved colors must agree. */
export const validateSgrUnderlineColorScreen = validateSgrAttrsScreen;

/** The requested explicit underline color of the pinned RGB and reset fixtures. */
export const requestedUnderlineColor = Object.freeze({ r: 255, g: 0, b: 128 });
/** Cube entries that equal each other when stored as literal bytes versus palette index 67. */
export const equalLiteral = Object.freeze({ r: 95, g: 135, b: 175 });
export const equalIndex = 67;
/** OSC 4 mutation of the indexed case: palette entries and the exact colors they are set to. */
export const mutation = Object.freeze({ 4: 0xff654321, 5: 0xff123456, [equalIndex]: 0xffaabbcc });
const mutationIndices = Object.freeze(Object.keys(mutation).map(Number));
const mutateFeed = Object.entries({ 4: 'rgb:65/43/21', 5: 'rgb:12/34/56', [equalIndex]: 'rgb:aa/bb/cc' })
    .map(([index, spec]) => `\x1b]4;${index};${spec}\x07`).join('');
const restoreFeed = mutationIndices.map(index => `\x1b]104;${index}\x07`).join('');

/** The pinned note texts, byte for byte. */
export const pinnedIndexedNotExposedNote = 'Palette control or underline cell state was not exposed';
export const pinnedIndexedIndistinguishableNote = 'Palette controls resolve to indistinguishable colors';
export const pinnedUncalibratedNote = 'Underline color readback was not calibrated for a negative';
export const pinnedRgbNotExposedNote = 'Underlined control, target, or target color was not exposed';
export const pinnedRgbDefaultMatchesNote = 'Default underline already matches the requested color';
export const pinnedResetNotExposedNote = 'Reset control or underline cell state was not exposed';
export const pinnedResetPrerequisiteNote = 'Requested colored underline prerequisite was not observed';
export const pinnedResetRepresentationNote = 'Reset-state color readback changed representation';

/** The pinned cell shape: the attribute suite's stored projection plus the underline color layers. */
export function sgrUnderlineColorCellAt(screen, row, col) {
    const cell = sgrColorCellAt(screen, row, col);
    const index = cell.underlineColorIndex, argb = cell.underlineArgb;
    const source = cell.underlineStyle === 0 ? 'none' : index >= 0 ? 'palette' : argb !== null ? 'literal' : 'default';
    const palette = screen.colorBinding.ansiPaletteArgb;
    const underlineColor = source === 'palette' ? argbRgb(palette[index]) : source === 'literal' ? argbRgb(argb) : null;
    // A default underline has no color of its own: it inherits the cell's text color at draw time.
    return { ...cell, underlineSource: source, underlineColor, underlineInheritsForeground: source === 'default' };
}

const eq = (a, b) => a !== null && b !== null && a !== undefined && b !== undefined && sameRgb(a, b);
/** No attribute except the underline flag and no overline: unrelated attribute drift. */
const quiet = cell => (cell.flags & 0xf7) === 0 && cell.overline === false;
/** Underline-color predicates over the stored fields (style 1 = single unless given). */
const ul = {
    default: (cell, style = 1) => cell.underlineStyle === style && cell.underlineSource === 'default',
    palette: (cell, index, style = 1) => cell.underlineStyle === style && cell.underlineSource === 'palette'
        && cell.underlineColorIndex === index && cell.underlineArgb === null,
    literal: (cell, rgb, style = 1) => cell.underlineStyle === style && cell.underlineSource === 'literal'
        && cell.underlineColorIndex === -1 && (cell.underlineArgb & 0xffffff) === ((rgb.r << 16) | (rgb.g << 8) | rgb.b),
    none: cell => cell.underlineStyle === 0 && cell.underlineSource === 'none',
};
/** Foreground/background setup predicates. */
const fg = { default: cell => isDefault(cell, 'fg'), palette: index => cell => isPalette(cell, 'fg', index),
    literal: (r, g, b) => cell => isLiteral(cell, 'fg', { r, g, b }) };
const bg = { default: cell => isDefault(cell, 'bg'), palette: index => cell => isPalette(cell, 'bg', index) };

/** One cell's setup: its character, expected style and fg/bg storage (the measured underline color is separate). */
const setup = (char, { style = 1, fgOk = fg.default, bgOk = bg.default } = {}) => ({ char, style, fgOk, bgOk });
/** A setup cell is valid when its character, quiet attributes, style and fg/bg storage hold; style 0 means no underline. */
const setupValid = (cell, spec) => cell.char === spec.char && quiet(cell) && cell.underlineStyle === spec.style
    && spec.fgOk(cell) && spec.bgOk(cell);
const allValid = (cells, specs) => cells.length === specs.length && specs.every((spec, index) => setupValid(cells[index], spec));

/** The clear between groups: SGR off, screen cleared, cursor home. */
const clear = sgrAttrsClear;
/** The labeled control cells shared by the cases: literal and index underline colors with equal resolved RGB, and a neutral cell. */
const literalControl = `\x1b[0m\x1b[4m\x1b[58;2;${equalLiteral.r};${equalLiteral.g};${equalLiteral.b}mP`;
const indexControl = `\x1b[0m\x1b[4m\x1b[58;5;${equalIndex}mQ`;
const neutralControl = '\x1b[0mN';

/** Case builder: pinned fixture, then control groups, then (indexed only) palette mutation and restoration reads. */
const case_ = ({ caseId, fixture, fixtureCells, controlFeed, mutated = false, ...fields }) => {
    const afterFeed = clear + fixture + '\x1b[0m' + controlFeed;
    return Object.freeze({
        caseId, probeId: caseId, fixture, fixtureCells, controlFeed, mutated, ...fields,
        pinnedPath: 'termless', pinnedRead: 0, pinnedFeeds: Object.freeze([fixture]),
        plan: Object.freeze([planFeed(sgrUnderlineColorReset), planFeed(fixture), planRead('cells'),
            planFeed(clear), planFeed(controlFeed), planRead('cells'),
            ...(mutated ? [planFeed(mutateFeed), planFeed(afterFeed), planRead('cells'),
                planFeed(restoreFeed), planFeed(afterFeed), planRead('cells')] : [])]),
    });
};

/** A compact observed record: stored text, flags, underline style/color layers and foreground per cell. */
const brief = cell => ({ char: cell.char, flags: cell.flags, underline: cell.underline, underlineStyle: cell.underlineStyle,
    underlineSource: cell.underlineSource, underlineColorIndex: cell.underlineColorIndex, underlineArgb: cell.underlineArgb,
    underlineColor: cell.underlineColor, underlineInheritsForeground: cell.underlineInheritsForeground,
    fg: cell.fg, fgSource: cell.fgSource, fgIndex: cell.fgIndex, bgSource: cell.bgSource, overline: cell.overline });
const briefObservation = observation => ({
    fixture: observation.fixture.map(brief), control: observation.control.map(brief),
    ...(observation.mutated ? { mutated: observation.mutated.map(brief), restored: observation.restored.map(brief) } : {}),
    labeledControls: observation.labeled, reverseVideo: observation.reverseVideo });

/** Layers of a verdict: each of stored and resolved is supported, unsupported or not-evaluated. */
const layers = (stored, resolved) => ({ stored, resolved });
const inconclusive = (note, state = layers('not-evaluated', 'not-evaluated')) => ({ outcome: 'inconclusive', note, layers: state });
const unsupported = (note, state) => ({ outcome: 'unsupported', note, layers: state });

/** The control cells shared by the indexed and RGB cases: literal/index pair with equal resolved RGB and a neutral cell. */
const sharedControlsValid = (control, offset) => {
    const [literal, indexed, neutral] = [control[offset], control[offset + 1], control[offset + 3]];
    return ul.literal(literal, equalLiteral) && ul.palette(indexed, equalIndex) && eq(literal.underlineColor, indexed.underlineColor)
        && neutral.underlineStyle === 0 && neutral.underlineSource === 'none' && quiet(neutral);
};

/** The palette must hold exactly the contracted start / mutated / restored state in every entry. */
function paletteStatesValid(observation) {
    const start = observation.baseline.ansiPaletteArgb;
    const states = [observation.bindings.fixture, observation.bindings.control];
    if (!states.every(binding => JSON.stringify(binding.ansiPaletteArgb) === JSON.stringify(start)
        && JSON.stringify(binding.paletteOverrideIndices) === JSON.stringify(observation.baseline.paletteOverrideIndices))) return false;
    const mutatedState = observation.bindings.mutated, restoredState = observation.bindings.restored;
    for (let index = 0; index < 256; index++) {
        const expected = mutationIndices.includes(index) ? mutation[index] : start[index];
        if (mutatedState.ansiPaletteArgb[index] !== expected || restoredState.ansiPaletteArgb[index] !== start[index]) return false;
    }
    const overrides = [...new Set([...observation.baseline.paletteOverrideIndices, ...mutationIndices])].sort((a, b) => a - b);
    return JSON.stringify(mutatedState.paletteOverrideIndices) === JSON.stringify(overrides)
        && JSON.stringify(restoredState.paletteOverrideIndices) === JSON.stringify(observation.baseline.paletteOverrideIndices)
        && observation.bindings.all.every(binding => !binding.reverseVideo);
}

// ---- indexed -------------------------------------------------------------------------------------------------
const indexedFixtureSetup = [setup('A', { fgOk: fg.palette(4) }), setup('X', { fgOk: fg.palette(4) }),
    setup('B', { fgOk: fg.palette(5) }), setup('Y', { fgOk: fg.palette(5) })];
const indexedControlSetup = [setup('P'), setup('Q'), setup('R', { fgOk: fg.palette(5) }), setup('N', { style: 0 }), setup('S')];
const indexedCase = case_({
    caseId: 'sgr.underline-color-indexed',
    fixture: '\x1b[4;38;5;4m\x1b[59mA\x1b[58;5;5mX\x1b[38;5;5m\x1b[59mB\x1b[58;5;4mY', fixtureCells: 4,
    controlFeed: `${literalControl}${indexControl}\x1b[0m\x1b[4;38;5;5m\x1b[58;5;5mR${neutralControl}\x1b[0m\x1b[4m\x1b[58:5:5mS`,
    controlCells: 5, mutated: true,
    pinnedExpected: 'SGR 58 index 5/4 underline colors match the opposite SGR 38 palette controls, not their same-foreground defaults',
    pinnedCriterion: 'X underline color equals the palette-5 foreground control B, Y equals the palette-4 control A',
    terminalFixture: { writes: ['\x1b[4m\x1b[58;5;5m'], demand: 'consumed (cursor advance does not verify SGR underline color)', minimum: { rows: 1, cols: 2 } },
    appearanceScope: 'stored underline style and palette index (never inferred from RGB), parent-derived resolved RGB through the recorded palette with an OSC 4 mutation and restoration, default-underline representation; rendered underline appearance is not claimed',
    labeledSpellings: ['SGR 58:5:5 (colon index form, cell S)'],
    grade(observation) {
        const { fixture: [a, x, b, y], control, reverseVideo } = observation;
        if (a.char !== 'A' || x.char !== 'X' || b.char !== 'B' || y.char !== 'Y') return inconclusive(pinnedIndexedNotExposedNote);
        if (reverseVideo !== false) return inconclusive('DECSCNM reverse video was not proven off, so stored colors cannot be separated from it');
        if (!allValid(observation.fixture, indexedFixtureSetup)) return inconclusive('Unrelated attributes, colors or the underline style drifted in the fixture, so the setup control is invalid');
        if (!allValid(control, indexedControlSetup) || !sharedControlsValid(control, 0)
            || !ul.palette(control[2], 5) || !eq(control[2].underlineColor, control[2].fg) || control[2].underlineInheritsForeground) {
            return inconclusive('The literal-versus-index, explicit-versus-default or neutral control cells were not measured as contracted');
        }
        if (!paletteStatesValid(observation) || !mutatedGroupsValid(observation)) {
            return inconclusive('The OSC 4 palette mutation and restoration controls were not measured as contracted');
        }
        if (eq(a.fg, b.fg)) return inconclusive(pinnedIndexedIndistinguishableNote);
        const storedOk = ul.default(a) && ul.palette(x, 5) && ul.default(b) && ul.palette(y, 4);
        const resolvedOk = eq(x.underlineColor, b.fg) && eq(y.underlineColor, a.fg) && a.underlineColor === null && b.underlineColor === null;
        if (storedOk && resolvedOk) return { outcome: 'supported', layers: layers('supported', 'supported') };
        const reasons = [];
        if (!ul.palette(x, 5)) reasons.push(`SGR 58;5;5 stored underline ${describe(x)} instead of palette index 5`);
        if (!ul.palette(y, 4)) reasons.push(`SGR 58;5;4 stored underline ${describe(y)} instead of palette index 4`);
        if (!ul.default(a) || !ul.default(b)) reasons.push('SGR 59 left an explicit underline color on a default cell');
        if (!reasons.length) reasons.push('the resolved underline colors did not equal the matching foreground palette controls');
        return unsupported(reasons.join('; '), layers(storedOk ? 'supported' : 'unsupported', resolvedOk ? 'supported' : 'unsupported'));
    },
});

/** Stored state is identical across the stable, mutated and restored groups; only the recorded palette differs,
 * and every palette-sourced color follows the group's own recorded palette (the App-reported foreground and the
 * derived underline alike). Independent of what the case measures, so a wrong stored index cannot mask the control. */
function mutatedGroupsValid({ fixture, control, mutated, restored, bindings }) {
    const stored = cell => JSON.stringify([cell.char, cell.flags, cell.underlineStyle, cell.underlineSource, cell.underlineColorIndex,
        cell.underlineArgb, cell.fgSource, cell.fgIndex, cell.foregroundArgb, cell.bgSource, cell.bgIndex, cell.backgroundArgb, cell.overline]);
    const stable = [...fixture, ...control];
    const reference = stable.map(stored);
    const sameStored = group => group.length === reference.length && group.every((cell, index) => stored(cell) === reference[index]);
    if (!sameStored(mutated) || !sameStored(restored)) return false;
    const follows = (group, binding) => group.every(cell => {
        const palette = index => argbRgb(binding.ansiPaletteArgb[index]);
        const fgOk = cell.fgSource !== 'palette' || eq(cell.fg, palette(cell.fgIndex));
        const ulOk = cell.underlineSource === 'palette' ? eq(cell.underlineColor, palette(cell.underlineColorIndex))
            : cell.underlineSource === 'literal' ? (cell.underlineArgb & 0xffffff) === ((cell.underlineColor.r << 16) | (cell.underlineColor.g << 8) | cell.underlineColor.b)
                : cell.underlineColor === null;
        return fgOk && ulOk;
    });
    if (!follows(mutated, bindings.mutated) || !follows(restored, bindings.restored)) return false;
    // Every cell stored at a mutated entry resolves to the new color while mutated, and to its original color again once restored.
    return stable.every((cell, index) => {
        const check = (value, storedIndex, source, field) => {
            if (source !== 'palette' || !mutationIndices.includes(storedIndex)) return true;
            const original = argbRgb(bindings.control.ansiPaletteArgb[storedIndex]);
            const changed = argbRgb(mutation[storedIndex]);
            return eq(mutated[index][field], changed) && !eq(mutated[index][field], original) && eq(restored[index][field], original)
                && eq(value, original);
        };
        return check(cell.fg, cell.fgIndex, cell.fgSource, 'fg')
            && check(cell.underlineColor, cell.underlineColorIndex, cell.underlineSource, 'underlineColor');
    });
}

/** A short description of a cell's stored underline color. */
function describe(cell) {
    if (cell.underlineSource === 'palette') return `palette index ${cell.underlineColorIndex}`;
    if (cell.underlineSource === 'literal') return `literal 0x${(cell.underlineArgb >>> 0).toString(16)}`;
    return cell.underlineSource === 'default' ? 'the default color' : 'no underline';
}

// ---- rgb -----------------------------------------------------------------------------------------------------
const rgbFixtureSetup = [setup('A', { fgOk: fg.literal(0, 0, 255) }), setup('B', { fgOk: fg.literal(0, 255, 0) }),
    setup('X', { fgOk: fg.literal(0, 255, 0) })];
const rgbControlSetup = [setup('P'), setup('Q'), setup('R', { fgOk: fg.literal(255, 0, 128) }), setup('N', { style: 0 }), setup('C'), setup('D')];
const rgbCase = case_({
    caseId: 'sgr.underline-color-rgb',
    fixture: '\x1b[4m\x1b[59m\x1b[38;2;0;0;255mA\x1b[38;2;0;255;0mB\x1b[58;2;255;0;128mX', fixtureCells: 3,
    controlFeed: `${literalControl}${indexControl}\x1b[0m\x1b[4m\x1b[38;2;255;0;128mR${neutralControl}`
        + '\x1b[0m\x1b[4m\x1b[58:2::255:0:128mC\x1b[0m\x1b[4m\x1b[58:2:0:255:0:128mD',
    controlCells: 6,
    pinnedExpected: 'sgr.underline-color-rgb: X has explicit rgb(255,0,128) underline distinct from the reset/default B cell',
    pinnedCriterion: 'X has explicit rgb(255,0,128) underline distinct from the reset/default B cell',
    terminalFixture: { writes: ['\x1b[4m\x1b[58;2;255;0;128mX\x1b[0m'], demand: 'consumed (cursor advance does not verify SGR styling)', minimum: { rows: 1, cols: 2 } },
    appearanceScope: 'stored underline style and exact literal underline bytes with no palette index, the default-underline representation distinct from the foreground; rendered underline appearance is not claimed',
    labeledSpellings: ['SGR 58:2::255:0:128 (colon, omitted colorspace, cell C)', 'SGR 58:2:0:255:0:128 (colon, explicit colorspace, cell D)'],
    grade(observation) {
        const { fixture: [a, b, x], control, reverseVideo } = observation;
        if (a.char !== 'A' || b.char !== 'B' || x.char !== 'X') return inconclusive(pinnedRgbNotExposedNote);
        if (reverseVideo !== false) return inconclusive('DECSCNM reverse video was not proven off, so stored colors cannot be separated from it');
        if (!allValid(observation.fixture, rgbFixtureSetup)) return inconclusive('Unrelated attributes, colors or the underline style drifted in the fixture, so the setup control is invalid');
        if (!allValid(control, rgbControlSetup) || !sharedControlsValid(control, 0)
            || !ul.default(control[2]) || control[2].underlineColor !== null || !control[2].underlineInheritsForeground) {
            return inconclusive('The literal-versus-index, foreground-substitution or neutral control cells were not measured as contracted');
        }
        if (eq(a.fg, b.fg)) return inconclusive('The foreground controls resolve to indistinguishable colors');
        if (eq(b.underlineColor, requestedUnderlineColor)) return inconclusive(pinnedRgbDefaultMatchesNote);
        const storedOk = ul.default(a) && ul.default(b) && ul.literal(x, requestedUnderlineColor);
        const resolvedOk = eq(x.underlineColor, requestedUnderlineColor) && a.underlineColor === null && b.underlineColor === null;
        if (storedOk && resolvedOk) return { outcome: 'supported', layers: layers('supported', 'supported') };
        const reasons = [];
        if (!ul.literal(x, requestedUnderlineColor)) reasons.push(`SGR 58;2;255;0;128 stored underline ${describe(x)} instead of the exact literal bytes`);
        if (!ul.default(a) || !ul.default(b)) reasons.push('SGR 59 left an explicit underline color on a control cell');
        if (!reasons.length) reasons.push('the resolved underline color was not rgb(255,0,128)');
        return unsupported(reasons.join('; '), layers(storedOk ? 'supported' : 'unsupported', resolvedOk ? 'supported' : 'unsupported'));
    },
});

// ---- reset ---------------------------------------------------------------------------------------------------
const resetFixtureSetup = [setup('C'), setup('X'), setup('Y')];
const resetControlSetup = [setup('S', { style: 3, fgOk: fg.palette(2), bgOk: bg.palette(4) }), setup('T', { style: 3, fgOk: fg.palette(2), bgOk: bg.palette(4) }),
    setup('U'), setup('V'), setup('N', { style: 0 })];
const resetCase = case_({
    caseId: 'sgr.underline-color-reset',
    fixture: '\x1b[4m\x1b[59mC\x1b[58;2;255;0;128mX\x1b[59mY', fixtureCells: 3,
    controlFeed: '\x1b[0m\x1b[4:3m\x1b[38;5;2m\x1b[48;5;4m\x1b[58;5;5mS\x1b[59mT\x1b[0m\x1b[4m\x1b[58;2;1;2;3mU\x1b[59mV' + neutralControl,
    controlCells: 5,
    pinnedExpected: 'X has explicit underline rgb(255,0,128); SGR 59 restores Y to the measured reset-state C color',
    pinnedCriterion: 'X has explicit underline rgb(255,0,128); SGR 59 restores Y to the measured reset-state C color',
    terminalFixture: { writes: ['\x1b[4m\x1b[58;2;255;0;128m\x1b[59m'], demand: 'consumed (cursor advance does not verify SGR styling)', minimum: { rows: 1, cols: 2 } },
    appearanceScope: 'stored default underline-color marker with no explicit index or literal after SGR 59, with underline style, foreground and background preserved; the default is an inherited-text-color marker, not a measured RGB, and rendered appearance is not claimed',
    labeledSpellings: ['SGR 59 after an indexed color on a curly underline with explicit foreground/background (cells S/T)', 'SGR 59 after a literal color on a single underline with default foreground/background (cells U/V)'],
    grade(observation) {
        const { fixture: [c, x, y], control, reverseVideo } = observation;
        if (c.char !== 'C' || x.char !== 'X' || y.char !== 'Y') return inconclusive(pinnedResetNotExposedNote);
        if (reverseVideo !== false) return inconclusive('DECSCNM reverse video was not proven off, so stored colors cannot be separated from it');
        if (!ul.literal(x, requestedUnderlineColor) || !ul.default(c)) return inconclusive(pinnedResetPrerequisiteNote);
        // SGR 59 must not clear the underline style: a cleared Y cannot measure the color reset.
        if (y.underlineStyle !== x.underlineStyle) return inconclusive('SGR 59 cleared or changed the underline style, so the color reset cannot be measured');
        if (!allValid(observation.fixture, resetFixtureSetup)) return inconclusive('Unrelated attributes or colors drifted in the fixture, so the setup control is invalid');
        const [s, t, u, v] = control;
        if (!allValid(control, resetControlSetup) || !ul.palette(s, 5, 3) || !ul.default(t, 3) || !ul.literal(u, { r: 1, g: 2, b: 3 }) || !ul.default(v)) {
            return inconclusive('The indexed/literal reset and style-preservation controls were not measured as contracted');
        }
        if (ul.default(y)) return { outcome: 'supported', layers: layers('supported', 'not-evaluated') };
        const note = ul.literal(y, requestedUnderlineColor) ? 'SGR 59 left the explicit rgb(255,0,128) underline color on Y'
            : `SGR 59 left Y with ${describe(y)} instead of the default underline color`;
        return unsupported(note, layers('unsupported', 'not-evaluated'));
    },
});

export const sgrUnderlineColorContracts = Object.freeze(
    [indexedCase, rgbCase, resetCase].map(contract => Object.freeze({ ...contract,
        observed: briefObservation, })));
/** Contract case ids in suite order. */
export const sgrUnderlineColorCaseIds = Object.freeze(sgrUnderlineColorContracts.map(contract => contract.caseId));

/** Project the read groups of one case into the grader's observation, with the recorded start binding. */
export function sgrUnderlineColorObservation(contract, screens, startBinding) {
    const expected = contract.mutated ? 4 : 2;
    if (!Array.isArray(screens) || screens.length !== expected || !startBinding) throw new Error(`Missing ${contract.caseId} read captures.`);
    const cells = (screen, count) => Array.from({ length: count }, (_, col) => sgrUnderlineColorCellAt(screen, 0, col));
    const groups = (screen, count) => cells(screen, count);
    const reverse = screens.map(screen => screen.colorBinding.reverseVideo);
    const observation = {
        fixture: groups(screens[0], contract.fixtureCells), control: groups(screens[1], contract.controlCells),
        reverseVideo: reverse.every(value => value === false) ? false : reverse.some(value => value === true) ? true : null,
        labeled: groups(screens[1], contract.controlCells).map(brief),
        baseline: { ansiPaletteArgb: startBinding.ansiPaletteArgb, paletteOverrideIndices: startBinding.paletteOverrideIndices },
    };
    if (contract.mutated) {
        observation.mutated = groups(screens[2], contract.fixtureCells + contract.controlCells);
        observation.restored = groups(screens[3], contract.fixtureCells + contract.controlCells);
        observation.bindings = { fixture: screens[0].colorBinding, control: screens[1].colorBinding,
            mutated: screens[2].colorBinding, restored: screens[3].colorBinding, all: screens.map(screen => screen.colorBinding) };
    }
    return observation;
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeSgrUnderlineColorCase(contract, observation, revision = sgrUnderlineColorContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown SGR-underline-color contracts revision ${revision}.`);
    if (!Array.isArray(observation?.fixture) || observation.fixture.length !== contract.fixtureCells
        || !Array.isArray(observation.control) || observation.control.length !== contract.controlCells
        || (contract.mutated && (!Array.isArray(observation.mutated) || !Array.isArray(observation.restored) || !observation.bindings))) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as sgrUnderlineColorFeeds, planReads as sgrUnderlineColorReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateSgrUnderlineColorFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}
