// Synthetic, fully valid SGR-RGB grading evidence for portable tests. Every
// frame, run-log entry and pinned result is produced through the same
// production validators used for real measurements; nothing here replaces the
// frozen real-App publication.
import { snapshotHash } from '../agent-observer.mjs';
import { sgrRgbContracts, sgrRgbReset, sgrRgbFeeds, sgrRgbCellAt,
    gradeSgrRgbCase, sgrRgbContractsVersion } from '../sgr-rgb-contracts.mjs';
import { replaySgrRgbPinned } from '../sgr-rgb-grading.mjs';
import { sgrRgbAdapterId } from '../sgr-rgb-grading-collector.mjs';

/** Synthetic row count, comfortably above the observer's floor. */
export const sgrRgbRows = 6;
/** Synthetic column count, comfortably above the observer's floor. */
export const sgrRgbCols = 60;
/** Total synthetic cell count. */
const cells = sgrRgbRows * sgrRgbCols;
/** A fixed synthetic pane identity. */
const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
/** The DA barrier every fenced operation records. */
const deviceAttribute = '\x1b[?62;4;22c';
/** Plain default-attribute run shape. */
const plain = { flags: 768, fg: -1, bg: -1 };

// A representative App-reported binding: light-gray on black defaults, an ANSI
// palette whose entries 1 and 4 are the theme's red and blue.
export const rgb = (r, g, b) => 0xff000000 + r * 65536 + g * 256 + b;
/** Light-gray default foreground. */
export const syntheticThemeFg = rgb(211, 211, 211);
/** Black default background. */
export const syntheticThemeBg = rgb(0, 0, 0);
/** The 256-entry palette: theme red at 1, blue at 4, gray elsewhere. */
export const syntheticPalette = () => Array.from({ length: 256 }, (_, index) =>
    index === 1 ? rgb(205, 0, 0) : index === 4 ? rgb(0, 0, 238) : rgb(136, 136, 136));

/** A representative App-reported binding with optional palette/default overrides. */
export function syntheticColorBinding(overrides = {}) {
    const palette = overrides.palette ?? syntheticPalette();
    return { stage: 'theme-resolution',
        stageNote: 'Per-channel storage-to-theme resolution exactly as the draw operation resolves cells: '
            + 'default -> theme default, palette index -> ANSI palette entry, literal -> stored ARGB; opaque alpha. '
            + 'Captured with the cells and theme under one buffer read lock. Precedes the inverse-cell swap, '
            + 'minimum-contrast adjustment, opacity compositing and glyph rasterisation, so rendered glyph '
            + 'appearance may still differ from these values.',
        themeName: 'Synthetic Dark', foregroundArgb: overrides.foreground ?? syntheticThemeFg,
        backgroundArgb: overrides.background ?? syntheticThemeBg, ansiPaletteArgb: palette,
        reverseVideo: false, foregroundOverride: false, backgroundOverride: false,
        paletteOverrideIndices: [], minimumContrast: 1 };
}

/** One resolved channel value. */
const channel = (source, index, argb) => ({ source, index, argb });
/** The binding's default foreground as a channel. */
const defaultFg = binding => channel('default', -1, binding.foregroundArgb);
/** The binding's default background as a channel. */
const defaultBg = binding => channel('default', -1, binding.backgroundArgb);

/** Stored cell-detail runs matching one attribute/resolved pair; literal colors
 * carry their stored ARGB bytes so literal coherence is fully exercisable. */
const detailRun = (count, flags, fg, bg, foregroundArgb = null, backgroundArgb = null) =>
    ({ count, flags, fg, bg, foregroundArgb, backgroundArgb,
        underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null });

/** Coalesce adjacent identical runs (every field but count) so fixtures stay canonical. */
function coalesceRuns(runs) {
    const key = run => JSON.stringify(Object.entries(run).filter(([field]) => field !== 'count').sort(([a], [b]) => a.localeCompare(b)));
    const coalesced = [];
    for (const run of runs) {
        const previous = coalesced.at(-1);
        if (previous && key(previous) === key(run)) previous.count += run.count;
        else coalesced.push(run);
    }
    return coalesced;
}

/** Attribute runs and resolved runs for the three contracted cells plus the
 * plain remainder, both coalesced canonically. */
export function syntheticSgrRgbParts(caseId, binding = syntheticColorBinding()) {
    const paletteFg = caseId === 'sgr.fg.standard';
    const indexFlag = paletteFg ? 0x4000 : 0x8000;
    const defaultFlag = paletteFg ? 0x0200 : 0x0100;
    const attributes = coalesceRuns([
        { count: 1, ...plain },
        { count: 1, flags: defaultFlag | indexFlag, fg: paletteFg ? 1 : -1, bg: paletteFg ? -1 : 1 },
        { count: 1, flags: defaultFlag | indexFlag, fg: paletteFg ? 4 : -1, bg: paletteFg ? -1 : 4 },
        { count: cells - 3, ...plain },
    ].map(run => ({ ...run })));
    const first = paletteFg ? channel('palette', 1, binding.ansiPaletteArgb[1]) : defaultFg(binding);
    const firstOther = paletteFg ? defaultBg(binding) : channel('palette', 1, binding.ansiPaletteArgb[1]);
    const second = paletteFg ? channel('palette', 4, binding.ansiPaletteArgb[4]) : defaultFg(binding);
    const secondOther = paletteFg ? defaultBg(binding) : channel('palette', 4, binding.ansiPaletteArgb[4]);
    const resolved = coalesceRuns([
        { count: 1, fg: defaultFg(binding), bg: defaultBg(binding) },
        { count: 1, fg: first, bg: firstOther },
        { count: 1, fg: second, bg: secondOther },
        { count: cells - 3, fg: defaultFg(binding), bg: defaultBg(binding) },
    ].map(run => ({ ...run })));
    const details = coalesceRuns([
        detailRun(1, plain.flags, -1, -1),
        detailRun(1, defaultFlag | indexFlag, paletteFg ? 1 : -1, paletteFg ? -1 : 1),
        detailRun(1, defaultFlag | indexFlag, paletteFg ? 4 : -1, paletteFg ? -1 : 4),
        detailRun(cells - 3, plain.flags, -1, -1),
    ]);
    return { attributes, resolved, details };
}

/** A complete valid RGB observation screen for one contracted case. */
export function syntheticSgrRgbScreen(caseId, binding = syntheticColorBinding()) {
    const { attributes, resolved, details } = syntheticSgrRgbParts(caseId, binding);
    return { rows: sgrRgbRows, cols: sgrRgbCols, lines: ['CXY', '', '', '', '', ''],
        cursorRow: 0, cursorCol: 3, cursorVisible: true,
        attributeRuns: attributes, resolvedColors: resolved, cellDetails: details, colorBinding: binding };
}

/** A screen whose fourth cell stores a literal color (SGR 38;2;1;2;3 style),
 * resolved to exactly its stored bytes; for literal-coherence assertions. */
export function syntheticSgrRgbLiteralScreen() {
    const binding = syntheticColorBinding();
    const { attributes, resolved, details } = syntheticSgrRgbParts('sgr.fg.standard', binding);
    const literalArgb = rgb(1, 2, 3);
    return { rows: sgrRgbRows, cols: sgrRgbCols, lines: ['CXYZ', '', '', '', '', ''],
        cursorRow: 0, cursorCol: 4, cursorVisible: true,
        attributeRuns: coalesceRuns([...attributes.slice(0, 3),
            { count: 1, flags: 0x0200, fg: -1, bg: -1 },
            { count: cells - 4, flags: plain.flags, fg: -1, bg: -1 }]),
        resolvedColors: coalesceRuns([...resolved.slice(0, 3),
            { count: 1, fg: channel('literal', -1, literalArgb), bg: defaultBg(binding) },
            { count: cells - 4, fg: defaultFg(binding), bg: defaultBg(binding) }]),
        cellDetails: coalesceRuns([...details.slice(0, 3),
            detailRun(1, 0x0200, -1, -1, literalArgb),
            detailRun(cells - 4, plain.flags, -1, -1)]),
        colorBinding: binding };
}

/** One journaled frame with its digest. */
function frame(index, kind, screenValue, barrier) {
    return { index, kind, paneId, barrier, screen: screenValue, sha256: snapshotHash(screenValue) };
}

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export function syntheticSgrRgbEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = syntheticColorBinding()) {
    const nonce = 'NTILDE_SR_' + runId;
    const blankParts = { attributes: [{ count: cells, ...plain }],
        resolved: [{ count: cells, fg: defaultFg(binding), bg: defaultBg(binding) }],
        details: [detailRun(cells, plain.flags, -1, -1)] };
    const blank = { rows: sgrRgbRows, cols: sgrRgbCols, lines: ['', '', '', '', '', ''],
        cursorRow: 0, cursorCol: 0, cursorVisible: true,
        attributeRuns: blankParts.attributes, resolvedColors: blankParts.resolved,
        cellDetails: blankParts.details, colorBinding: binding };
    const bindScreen = { ...blank, lines: [nonce, '', '', '', '', ''], cursorCol: nonce.length };
    const frames = [frame(0, 'sgrgb-start', blank, null),
        frame(1, 'sgrgb-bind', bindScreen, deviceAttribute)];
    const cases = sgrRgbContracts.map(contract => {
        const feeds = sgrRgbFeeds(contract);
        const barriers = feeds.map(() => deviceAttribute);
        const screens = [syntheticSgrRgbScreen(contract.caseId, binding)];
        const readScreen = screens[0];
        frames.push(frame(frames.length, 'sgrgb-read', readScreen, deviceAttribute));
        const pinned = { probeId: contract.caseId, ...replaySgrRgbPinned(contract, screens) };
        return { caseId: contract.caseId, feeds, barriers,
            reads: [{ kind: 'cells', cursor: [readScreen.cursorRow, readScreen.cursorCol] }], pinned };
    });
    frames.push(frame(frames.length, 'sgrgb-finish', blank, deviceAttribute));
    const run = { schemaVersion: 1, adapterId: sgrRgbAdapterId,
        probeIds: sgrRgbContracts.map(contract => contract.caseId), cases };
    const adapter = { id: sgrRgbAdapterId, derivedFromAdapterId: 'ntilde-owned-sgr-colors-grading-v1',
        bundleSha256, upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, reset: sgrRgbReset };
}

/** Graded cells of one synthetic case for direct assertions. */
export function syntheticSgrRgbCells(caseId, binding) {
    const screenValue = syntheticSgrRgbScreen(caseId, binding);
    return [0, 1, 2].map(col => sgrRgbCellAt(screenValue, 0, col));
}

/** The independent contract verdict for one synthetic case. */
export function syntheticSgrRgbGrade(caseId, binding) {
    const contract = sgrRgbContracts.find(item => item.caseId === caseId);
    return gradeSgrRgbCase(contract, syntheticSgrRgbCells(caseId, binding), sgrRgbContractsVersion);
}
