// Independent selection-color model of the OSC 17 / 19 / 117 / 119 qualification. It is written from the xterm contract for OSC 17/19
// (selection background/foreground set and query) and OSC 117/119 (selection background/foreground reset), shares no code with the VT
// parser, and is the oracle for what each stage must show: the overrides the exact bytes written so far leave behind. Expected
// restoration is never taken from the App's own resolver; it is the baseline the suite measured before it mutated anything, with the
// model's overrides laid over it. Unlike the foreground, background and cursor defaults, an unset selection source stays `null`: the
// model demands the absence of a concrete reply, never an invented RGB value. Color-spec parsing is shared with the runtime-color
// reset model (a digest-checked measured module); every selection semantic lives here.
import { parseColorSpec, oscStrings, opaqueArgb } from './color-reset-model.mjs';

export { parseColorSpec, opaqueArgb, channels16, hexOf } from './color-reset-model.mjs';

/** The empty override state: nothing runtime-set. Selection sources are nullable in the baseline. */
export const noOverrides = () => ({ palette: new Map(), foreground: null, background: null, cursor: null,
    selectionBackground: null, selectionForeground: null });

const dynamicKey = { 10: 'foreground', 11: 'background', 12: 'cursor', 17: 'selectionBackground', 19: 'selectionForeground' };
const resetKey = { 110: 'foreground', 111: 'background', 112: 'cursor', 117: 'selectionBackground', 119: 'selectionForeground' };

/** OSC 4: set the palette slots named by (index, spec) pairs; a query pair and an invalid pair change nothing. */
function applyPalette(next, params) {
    for (let i = 0; i + 1 < params.length; i += 2) {
        const index = Number(params[i]), value = params[i + 1] === '?' ? null : parseColorSpec(params[i + 1]);
        if (Number.isInteger(index) && index >= 0 && index < 256 && value !== null) next.palette.set(index, value);
    }
}
/** OSC 104: reset the named slots, or every slot when the string names none. */
function resetPalette(next, params) {
    if (params.length === 0) next.palette.clear();
    else for (const item of params) next.palette.delete(Number(item));
}
/** One OSC string applied to `next`; queries change nothing, invalid values change nothing and other codes are ignored.
 * A dynamic set takes its color from the first parameter only; a reset clears its channel whatever parameters follow. */
function applyOsc(next, { code, params }) {
    if (code === '4') return applyPalette(next, params);
    if (code === '104') return resetPalette(next, params);
    if (Object.hasOwn(dynamicKey, code)) {
        const value = parseColorSpec(params[0] ?? '');
        if (value !== null) next[dynamicKey[code]] = value;
    } else if (Object.hasOwn(resetKey, code)) next[resetKey[code]] = null;
}

/** Apply every OSC color string of `bytes` to a copy of `state`. */
export function applyColorWrites(state, bytes) {
    const next = { palette: new Map(state.palette), foreground: state.foreground, background: state.background, cursor: state.cursor,
        selectionBackground: state.selectionBackground, selectionForeground: state.selectionForeground };
    for (const item of oscStrings(bytes)) applyOsc(next, item);
    return next;
}

/** The overrides after each stage's writes, cumulatively; `stages` is a list of write lists. */
export function overridesByStage(stages) {
    let state = noOverrides();
    return stages.map(writes => {
        for (const bytes of writes) state = applyColorWrites(state, bytes);
        return state;
    });
}

/** The effective colors of a stage: the measured baseline with the model's overrides laid over it. A selection source with no override
 * keeps its measured baseline value, which may be `null` (unset) — never a substituted RGB value. */
export function effectiveColors(baseline, overrides) {
    const over = (baselineValue, override) => override === null ? baselineValue : opaqueArgb(override);
    return {
        foreground: over(baseline.foreground, overrides.foreground),
        background: over(baseline.background, overrides.background),
        cursor: over(baseline.cursor, overrides.cursor),
        selectionBackground: over(baseline.selectionBackground, overrides.selectionBackground),
        selectionForeground: over(baseline.selectionForeground, overrides.selectionForeground),
        palette: baseline.palette.map((color, index) => (overrides.palette.has(index) ? opaqueArgb(overrides.palette.get(index)) : color)),
    };
}

/** The sorted palette indices the model says are overridden. */
export const overrideIndices = overrides => [...overrides.palette.keys()].sort((a, b) => a - b);

/** The color a query spec asks for, out of effective colors. Selection queries return `null` for an unset source: the model then
 * demands the explicit absence of a reply, behind the exchange's completed ordering barrier. */
export function queriedColor(effective, spec) {
    if (spec.code === 4) return effective.palette[spec.index];
    return effective[dynamicKey[spec.code]];
}

/** One explicit cell's expected resolved channel: a default marker resolves to the effective default, a palette index to its effective
 * slot, literal RGB to itself. */
export function channelMatches(actual, wanted, effective, defaultKey) {
    if (actual.source !== wanted.source) return false;
    if (wanted.source === 'default') return actual.argb === effective[defaultKey];
    if (wanted.source === 'palette') return actual.index === wanted.index && actual.argb === effective.palette[wanted.index];
    return actual.argb === opaqueArgb(wanted.rgb);
}

/** Every explicit cell's resolved channels match the model: default markers to effective defaults, palette indices to effective slots, literal RGB to itself. */
export function cellsMatch(screen, cells, effective, expandResolvedColors) {
    const resolved = expandResolvedColors(screen.resolvedColors, screen.rows * screen.cols, screen.colorBinding);
    return cells.every(cell => {
        const value = resolved[cell.row * screen.cols + cell.col];
        return channelMatches(value.fg, cell.fg, effective, 'foreground') && channelMatches(value.bg, cell.bg, effective, 'background');
    });
}
