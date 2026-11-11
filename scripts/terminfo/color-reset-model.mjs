// Independent runtime-color model of the OSC reset qualification. It is written from the xterm contract for OSC 4/10/11/12 (set and
// query) and OSC 104/110/111/112 (reset), shares no code with the VT parser, and is the oracle for what each stage must show: the
// overrides the exact bytes written so far leave behind. Expected restoration is never taken from the App's own resolver; it is the
// baseline the suite measured before it mutated anything, with the model's overrides laid over it.
const esc = '\x1b', bel = '\x07';

/** One 8-bit channel of an xterm color spec channel of one to four hex digits (the value scaled to eight bits, as xterm does). */
function channel(text) {
    if (!/^[0-9a-fA-F]{1,4}$/.test(text)) return null;
    return Math.floor(Number.parseInt(text, 16) * 255 / (16 ** text.length - 1));
}

/** A color spec (`#rrggbb` or `rgb:r/g/b`, one to four hex digits per channel) as 0xRRGGBB, or null. */
export function parseColorSpec(spec) {
    let parts = null;
    if (/^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{9}|[0-9a-fA-F]{12})$/.test(spec)) {
        const width = (spec.length - 1) / 3;
        parts = [spec.slice(1, 1 + width), spec.slice(1 + width, 1 + 2 * width), spec.slice(1 + 2 * width)];
    } else if (spec.startsWith('rgb:')) parts = spec.slice(4).split('/');
    if (parts?.length !== 3) return null;
    const channels = parts.map(channel);
    return channels.includes(null) ? null : (channels[0] << 16) | (channels[1] << 8) | channels[2];
}

/** The OSC strings (code and parameter text) of a byte string, in order; a string is terminated by BEL or ST. */
export function oscStrings(bytes) {
    const found = [];
    for (let at = bytes.indexOf(esc + ']'); at >= 0; at = bytes.indexOf(esc + ']', at + 2)) {
        let end = at + 2;
        while (end < bytes.length && bytes[end] !== bel && !(bytes[end] === esc && bytes[end + 1] === '\\')) end++;
        if (end >= bytes.length) break;
        const body = bytes.slice(at + 2, end), split = body.indexOf(';');
        found.push({ code: split < 0 ? body : body.slice(0, split), params: split < 0 ? [] : body.slice(split + 1).split(';') });
    }
    return found;
}

/** The empty override state: nothing runtime-set. */
export const noOverrides = () => ({ palette: new Map(), foreground: null, background: null, cursor: null });

const dynamicKey = { 10: 'foreground', 11: 'background', 12: 'cursor' };

/** Apply every OSC color string of `bytes` to a copy of `state` (queries change nothing; other codes are ignored). */
export function applyColorWrites(state, bytes) {
    const next = { palette: new Map(state.palette), foreground: state.foreground, background: state.background, cursor: state.cursor };
    for (const { code, params } of oscStrings(bytes)) {
        if (code === '4') {
            for (let i = 0; i + 1 < params.length; i += 2) {
                const index = Number(params[i]), value = params[i + 1] === '?' ? null : parseColorSpec(params[i + 1]);
                if (Number.isInteger(index) && index >= 0 && index < 256 && value !== null) next.palette.set(index, value);
            }
        } else if (Object.hasOwn(dynamicKey, code)) {
            const value = parseColorSpec(params[0] ?? '');
            if (value !== null) next[dynamicKey[code]] = value;
        } else if (code === '104') {
            if (params.length === 0) next.palette.clear();
            else for (const item of params) next.palette.delete(Number(item));
        } else if (code === '110') next.foreground = null;
        else if (code === '111') next.background = null;
        else if (code === '112') next.cursor = null;
    }
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

/** An opaque ARGB integer from 0xRRGGBB. */
export const opaqueArgb = rgb => (0xff000000 | rgb) >>> 0;
/** The three 16-bit channels xterm reports for an ARGB value (each eight-bit channel repeated: x 257). */
export const channels16 = argb => [(argb >>> 16) & 255, (argb >>> 8) & 255, argb & 255].map(value => value * 257);
/** A printable `rgb:` spelling of 0xRRGGBB or ARGB. */
export const hexOf = argb => '#' + (argb & 0xffffff).toString(16).padStart(6, '0');

/** The effective colors of a stage: the measured baseline with the model's overrides laid over it. */
export function effectiveColors(baseline, overrides) {
    return {
        foreground: overrides.foreground === null ? baseline.foreground : opaqueArgb(overrides.foreground),
        background: overrides.background === null ? baseline.background : opaqueArgb(overrides.background),
        cursor: overrides.cursor === null ? baseline.cursor : opaqueArgb(overrides.cursor),
        palette: baseline.palette.map((color, index) => (overrides.palette.has(index) ? opaqueArgb(overrides.palette.get(index)) : color)),
    };
}

/** The sorted palette indices the model says are overridden. */
export const overrideIndices = overrides => [...overrides.palette.keys()].sort((a, b) => a - b);

/** The color a query spec asks for, out of effective colors. */
export function queriedColor(effective, spec) {
    if (spec.code === 4) return effective.palette[spec.index];
    return effective[dynamicKey[spec.code]];
}
