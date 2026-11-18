// Mintty OSC7770/7777 queries return the current integer typographic point size.
// Mutation, X11 font selection, fractional point replies and glyph pixels are outside this contract.
import { cellSizeGeometry, cellSizeMatches } from './cell-size-contracts.mjs';
export const fontSizeSuiteId = 'ntilde-owned-osc-font-size-v1';
export const fontSizeEffectId = 'ntilde-osc-font-size-query-effect-v1';
export const fontSizeVersion = 1;
export const fontSizePrefix = 'fnt';
export const fontSizeNonce = 'NTILDE_FONT_';
export const fontSizeIds = Object.freeze(['extensions.osc7770-font-size', 'extensions.osc7777-font-window-size']);
const osc = (body, end = '\x07') => '\x1b]' + body + end;
export const fontSizePinnedRequests = Object.freeze([osc('7770;?'), osc('7777;?')]);
export const fontSizeContracts = Object.freeze(fontSizeIds.map((caseId, index) => {
    const code = index === 0 ? '7770' : '7777';
    return { caseId, exchanges: [
        { id: 'positive-before', role: 'control', request: '\x1b[16t', protocol: 'csi' },
        { id: 'pinned', role: 'pinned', request: fontSizePinnedRequests[index], protocol: code, terminator: '\x07' },
        { id: 'documented-bel', role: 'required', request: osc(code + ';?'), protocol: code, terminator: '\x07' },
        { id: 'documented-st', role: 'required', request: osc(code + ';?', '\x1b\\'), protocol: code, terminator: '\x1b\\' },
        ...['', ';', ';??', ';?;1', ';12', ';+2', ';-2'].map((tail, n) =>
            ({ id: `unavailable-form-${n}`, role: 'required', request: osc(code + tail), protocol: 'silence' })),
        { id: 'positive-after', role: 'control', request: '\x1b[16t', protocol: 'csi' },
    ] };
}));

// Independent binary64 conversion-error bound: eight ULPs at the nearest
// positive integer, not rounding of meaningful fractional point sizes.
export function fontSizeIntegerPoints(geometry) {
    const points = geometry.fontSizeDip * geometry.renderScaling / geometry.backingScale * 0.75;
    const nearest = Math.round(points);
    const ulp = 2 ** (Math.floor(Math.log2(nearest)) - 52);
    if (!Number.isFinite(points) || nearest < 1 || nearest > 512 || Math.abs(points - nearest) > 8 * ulp) {
        throw new Error('Font query contract requires an integer point size within conversion roundoff.');
    }
    return nearest;
}

export function fontSizeGeometry(value, rows, cols) {
    const geometry = cellSizeGeometry(value, rows, cols);
    for (const key of ['fontSizeDip', 'windowWidthDip', 'windowHeightDip']) {
        if (!Number.isFinite(value?.[key]) || value[key] <= 0 || value[key] > 32768) throw new Error('Missing or invalid observed font/window geometry.');
        geometry[key] = value[key];
    }
    fontSizeIntegerPoints(geometry);
    return geometry;
}

export function fontSizeMatches(exchange, replies, geometry) {
    if (exchange.protocol === 'csi' || exchange.protocol === 'silence') return cellSizeMatches(exchange, replies, geometry);
    const points = fontSizeIntegerPoints(geometry);
    return replies.length === 1 && replies[0] === osc(`${exchange.protocol};${points}`, exchange.terminator);
}

export function replayFontSizePinned(index, raw) {
    const code = index === 0 ? '7770' : '7777';
    const pattern = index === 0 ? /\x1b\]7770;[0-9]+(?:\x07|\x1b\\)/ : /\x1b\]7777;[0-9]+(?:\x07|\x1b\\)/; // NOSONAR - frozen OSC framing bytes
    const frame = pattern.exec(raw)?.[0];
    if (frame) return { pass: true, response: raw, observation: { outcome: 'supported', evidence: 'query' }, assertions: [{ kind: 'positive',
        expected: index === 0 ? 'Complete OSC 7770 font-size reply' : 'Complete OSC 7777 window-size reply', observed: frame }] };
    return { pass: false, response: raw, observation: { outcome: 'inconclusive', reason: raw.includes('\x1b]' + code + ';') ? 'invalid-reply' : 'no-response', evidence: 'query' } };
}
