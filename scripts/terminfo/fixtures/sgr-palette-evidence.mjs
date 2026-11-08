// Synthetic, fully valid SGR-palette grading evidence for portable tests. Every
// frame, run-log entry and pinned result is produced through the same
// production validators used for real measurements; nothing here replaces the
// frozen real-App publication.
import { snapshotHash } from '../agent-observer.mjs';
import { sgrPaletteContracts, sgrPaletteReset, sgrPaletteFeeds, sgrPaletteObservation,
    gradeSgrPaletteCase } from '../sgr-palette-contracts.mjs';
import { replaySgrPalettePinned } from '../sgr-palette-grading.mjs';
import { sgrPaletteAdapterId } from '../sgr-palette-grading-collector.mjs';
import { syntheticColorBinding, rgb } from './sgr-rgb-evidence.mjs';

/** Synthetic row count, comfortably above the observer's floor. */
export const sgrPaletteRows = 6;
/** Synthetic column count, comfortably above the observer's floor. */
export const sgrPaletteCols = 60;
const cells = sgrPaletteRows * sgrPaletteCols;
/** A fixed synthetic pane identity. */
const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
/** The DA barrier every fenced operation records. */
const deviceAttribute = '\x1b[?62;4;22c';
/** Attribute flag bits the fixtures set. */
const attrBits = { bold: 0x01, inverse: 0x02, italic: 0x04, underline: 0x08, strikethrough: 0x10, blink: 0x20, faint: 0x40, hidden: 0x80 };
/** Default and palette marker bits per channel. */
const markers = { fg: { default: 0x100, palette: 0x4000 }, bg: { default: 0x200, palette: 0x8000 } };

/** The 16 standard/bright colors of a representative xterm-style theme; all distinct. */
const base16 = [[0, 0, 0], [205, 0, 0], [0, 205, 0], [205, 205, 0], [0, 0, 238], [205, 0, 205], [0, 205, 205], [229, 229, 229],
    [127, 127, 127], [255, 0, 0], [0, 255, 0], [255, 255, 0], [92, 92, 255], [255, 0, 255], [0, 255, 255], [255, 255, 255]];
const cubeLevels = [0, 95, 135, 175, 215, 255];
/** The canonical 256-entry xterm palette: 16 theme colors, the 6x6x6 cube, then 24 grays. */
export const canonicalPalette = () => Array.from({ length: 256 }, (_, index) => {
    if (index < 16) return rgb(...base16[index]);
    if (index < 232) {
        const n = index - 16;
        return rgb(cubeLevels[Math.floor(n / 36)], cubeLevels[Math.floor(n / 6) % 6], cubeLevels[n % 6]);
    }
    const level = 8 + (index - 232) * 10;
    return rgb(level, level, level);
});
/** A representative theme: light-gray text on black, canonical palette. */
export const paletteBinding = (overrides = {}) => syntheticColorBinding({ palette: canonicalPalette(), ...overrides });

/** Channel specs. */
export const defaultChannel = Object.freeze({ kind: 'default' });
export const paletteChannel = index => ({ kind: 'palette', index });
export const literalChannel = (r, g, b) => ({ kind: 'literal', rgb: { r, g, b } });
/** A synthetic cell spec. */
export const colorCell = (char, { fg = defaultChannel, bg = defaultChannel, attrs = [] } = {}) => ({ char, fg, bg, attrs });
/** A plain neutral cell. */
export const plainCell = char => colorCell(char);

/** Coalesce adjacent identical runs (every field but count) so fixtures stay canonical. */
function coalesce(runs) {
    const key = run => JSON.stringify(Object.entries(run).filter(([field]) => field !== 'count').sort(([a], [b]) => a.localeCompare(b)));
    const out = [];
    for (const run of runs) {
        const previous = out.at(-1);
        if (previous && key(previous) === key(run)) previous.count += run.count;
        else out.push({ ...run });
    }
    return out;
}

const literalArgb = rgbValue => 0xff000000 + rgbValue.r * 65536 + rgbValue.g * 256 + rgbValue.b;

/** Flags, stored index and literal bytes of one channel spec. */
function stored(spec, channel) {
    if (spec.kind === 'default') return { flag: markers[channel].default, index: -1, literal: null };
    if (spec.kind === 'palette') return { flag: markers[channel].palette, index: spec.index, literal: null };
    return { flag: 0, index: -1, literal: literalArgb(spec.rgb) };
}
/** The resolved channel of one spec under a binding. */
function resolvedChannel(spec, channel, binding) {
    const field = channel === 'fg' ? 'foregroundArgb' : 'backgroundArgb';
    if (spec.kind === 'default') return { source: 'default', index: -1, argb: binding[field] };
    if (spec.kind === 'palette') return { source: 'palette', index: spec.index, argb: binding.ansiPaletteArgb[spec.index] };
    return { source: 'literal', index: -1, argb: literalArgb(spec.rgb) };
}

/** A complete valid observation screen with the given leading cells; the rest is plain. */
export function syntheticSgrPaletteScreen(leading, binding = paletteBinding()) {
    const plainSpec = colorCell(' ');
    const specs = [...leading, ...Array.from({ length: cells - leading.length }, () => plainSpec)];
    const parts = specs.map(spec => {
        const fg = stored(spec.fg, 'fg'), bg = stored(spec.bg, 'bg');
        const flags = spec.attrs.reduce((value, name) => value | attrBits[name === 'dim' ? 'faint' : name], 0) | fg.flag | bg.flag;
        return {
            attribute: { count: 1, flags, fg: fg.index, bg: bg.index },
            resolved: { count: 1, fg: resolvedChannel(spec.fg, 'fg', binding), bg: resolvedChannel(spec.bg, 'bg', binding) },
            detail: { count: 1, flags, fg: fg.index, bg: bg.index, foregroundArgb: fg.literal, backgroundArgb: bg.literal,
                overline: spec.attrs.includes('overline'), underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null },
        };
    });
    return { rows: sgrPaletteRows, cols: sgrPaletteCols,
        lines: [leading.map(spec => spec.char).join(''), '', '', '', '', ''],
        cursorRow: 0, cursorCol: leading.length, cursorVisible: true,
        attributeRuns: coalesce(parts.map(part => part.attribute)),
        resolvedColors: coalesce(parts.map(part => part.resolved)),
        cellDetails: coalesce(parts.map(part => part.detail)), colorBinding: binding };
}

/** The cell specs of the two reads of one synthetic case (a conclusive, supported run). */
export function syntheticSgrPaletteReads(caseId) {
    const channel = caseId.includes('.fg.') ? 'fg' : 'bg';
    const other = channel === 'fg' ? 'bg' : 'fg';
    const own = spec => ({ [channel]: spec });
    const both = (spec, otherSpec) => ({ [channel]: spec, [other]: otherSpec });
    if (caseId.endsWith('.bright')) {
        return {
            fixture: [plainCell('C'), colorCell('X', own(paletteChannel(9))), colorCell('Y', own(paletteChannel(12)))],
            control: [colorCell('P', { ...own(paletteChannel(1)), attrs: ['bold'] }), colorCell('Q', own(paletteChannel(9))), plainCell('R')],
        };
    }
    if (caseId.endsWith('.256')) {
        return {
            fixture: [plainCell('C'), colorCell('A', own(literalChannel(95, 135, 175))), colorCell('B', own(literalChannel(215, 135, 95))),
                colorCell('X', own(paletteChannel(67))), colorCell('Y', own(paletteChannel(173)))],
            control: [colorCell('P', own(paletteChannel(0))), colorCell('Q', own(paletteChannel(255))), plainCell('R')],
        };
    }
    const setup = channel === 'fg' ? 1 : 2;
    return {
        fixture: [plainCell('C'), colorCell('X', own(paletteChannel(setup))), plainCell('R')],
        control: [colorCell('A', { fg: paletteChannel(1), bg: paletteChannel(2) }),
            colorCell('B', both(defaultChannel, paletteChannel(other === 'fg' ? 1 : 2))), plainCell('R')],
    };
}

/** Screens (fixture, control) of one synthetic case. */
export function syntheticSgrPaletteScreens(caseId, binding = paletteBinding(), readsOf = null) {
    const reads = readsOf?.(caseId) ?? syntheticSgrPaletteReads(caseId);
    return [reads.fixture, reads.control].map(specs => syntheticSgrPaletteScreen(specs, binding));
}

/** One journaled frame with its digest. */
function frame(index, kind, screenValue, barrier) {
    return { index, kind, paneId, barrier, screen: screenValue, sha256: snapshotHash(screenValue) };
}

/** A complete, valid synthetic run: frames, worker run log and adapter facts.
 * `readsOf(caseId)` may override the per-case cell specs to build negative evidence. */
export function syntheticSgrPaletteEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64),
    binding = paletteBinding(), readsOf = null) {
    const nonce = 'NTILDE_SP_' + runId;
    const blank = syntheticSgrPaletteScreen([], binding);
    const bindScreen = { ...syntheticSgrPaletteScreen([], binding), lines: [nonce, '', '', '', '', ''], cursorCol: nonce.length };
    const frames = [frame(0, 'sgpal-start', blank, null), frame(1, 'sgpal-bind', bindScreen, deviceAttribute)];
    const cases = sgrPaletteContracts.map(contract => {
        const feeds = sgrPaletteFeeds(contract);
        const barriers = feeds.map(() => deviceAttribute);
        const screens = syntheticSgrPaletteScreens(contract.caseId, binding, readsOf);
        for (const screen of screens) frames.push(frame(frames.length, 'sgpal-read', screen, deviceAttribute));
        const pinned = { probeId: contract.caseId, ...replaySgrPalettePinned(contract, screens) };
        return { caseId: contract.caseId, feeds, barriers,
            reads: screens.map(screen => ({ kind: 'cells', cursor: [screen.cursorRow, screen.cursorCol] })), pinned };
    });
    frames.push(frame(frames.length, 'sgpal-finish', blank, deviceAttribute));
    const run = { schemaVersion: 1, adapterId: sgrPaletteAdapterId,
        probeIds: sgrPaletteContracts.map(contract => contract.caseId), cases };
    const adapter = { id: sgrPaletteAdapterId, derivedFromAdapterId: 'ntilde-owned-sgr-attributes-grading-v1',
        bundleSha256, upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, reset: sgrPaletteReset };
}

/** The independent contract verdict for one synthetic case. */
export function syntheticSgrPaletteGrade(caseId, binding = paletteBinding(), readsOf = null) {
    const contract = sgrPaletteContracts.find(item => item.caseId === caseId);
    return gradeSgrPaletteCase(contract, sgrPaletteObservation(contract, syntheticSgrPaletteScreens(caseId, binding, readsOf)));
}
