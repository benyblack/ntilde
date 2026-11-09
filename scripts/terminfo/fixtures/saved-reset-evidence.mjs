// Synthetic, fully valid saved-state / reset grading evidence for portable tests. Every frame, run-log entry and
// pinned result is produced through the same production validators used for real measurements. The expected screens
// are written out literally here, independently of the contracts, so the fixtures are a second oracle, not a mirror.
import { snapshotHash } from '../agent-observer.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { savedResetContracts, savedResetObservation, gradeSavedResetCase } from '../saved-reset-contracts.mjs';
import { replaySavedResetPinned } from '../saved-reset-grading.mjs';
import { savedResetAdapterId } from '../saved-reset-grading-collector.mjs';
import { paletteBinding, colorCell, paletteChannel, literalChannel } from './sgr-palette-evidence.mjs';

export { paletteBinding, colorCell, paletteChannel, literalChannel } from './sgr-palette-evidence.mjs';

/** Synthetic geometry, above the contracts' floor. */
export const savedResetRows = 10;
export const savedResetCols = 60;
const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';
const attrBits = { bold: 0x01, inverse: 0x02, italic: 0x04, underline: 0x08, strikethrough: 0x10, blink: 0x20, faint: 0x40, hidden: 0x80 };
const markers = { fg: { default: 0x100, palette: 0x4000 }, bg: { default: 0x200, palette: 0x8000 } };
/** The 0xAARRGGBB value of a literal color. */
const literalArgb = rgb => 0xff000000 + rgb.r * 65536 + rgb.g * 256 + rgb.b;

/** Stored marker flag, palette index and literal bytes of one channel spec. */
const stored = (spec, channel) => {
    if (spec.kind === 'default') return { flag: markers[channel].default, index: -1, literal: null };
    if (spec.kind === 'palette') return { flag: markers[channel].palette, index: spec.index, literal: null };
    return { flag: 0, index: -1, literal: literalArgb(spec.rgb) };
};
/** The resolved channel of one spec under a binding. */
const resolvedChannel = (spec, channel, binding) => {
    const field = channel === 'fg' ? 'foregroundArgb' : 'backgroundArgb';
    if (spec.kind === 'default') return { source: 'default', index: -1, argb: binding[field] };
    if (spec.kind === 'palette') return { source: 'palette', index: spec.index, argb: binding.ansiPaletteArgb[spec.index] };
    return { source: 'literal', index: -1, argb: literalArgb(spec.rgb) };
};
/** Coalesce adjacent identical runs (every field but count) so fixtures stay canonical. */
const coalesce = runs => {
    const key = run => JSON.stringify(Object.entries(run).filter(([field]) => field !== 'count').sort(([a], [b]) => a.localeCompare(b)));
    const out = [];
    for (const run of runs) {
        const previous = out.at(-1);
        if (previous && key(previous) === key(run)) previous.count += run.count;
        else out.push({ ...run });
    }
    return out;
};

/** One screen: `cells` are `{ row, col, spec }` placements over a plain blank grid; `fill` replaces every cell with one spec. */
export function syntheticSavedResetScreen({ cells = [], cursor = [0, 0], alt = false, fill = null, buffer = {}, history = [] } = {}, binding = paletteBinding()) {
    const grid = Array.from({ length: savedResetRows * savedResetCols }, () => fill ?? colorCell(' '));
    for (const { row, col, spec } of cells) grid[row * savedResetCols + col] = spec;
    const parts = grid.map(spec => {
        const fg = stored(spec.fg, 'fg'), bg = stored(spec.bg, 'bg');
        const flags = spec.attrs.reduce((value, name) => value | attrBits[name === 'dim' ? 'faint' : name], 0) | fg.flag | bg.flag | (spec.underlineStyle ? attrBits.underline : 0);
        return { attribute: { count: 1, flags, fg: fg.index, bg: bg.index },
            resolved: { count: 1, fg: resolvedChannel(spec.fg, 'fg', binding), bg: resolvedChannel(spec.bg, 'bg', binding) },
            detail: { count: 1, flags, fg: fg.index, bg: bg.index, foregroundArgb: fg.literal, backgroundArgb: bg.literal, overline: spec.attrs.includes('overline'),
                underlineStyle: spec.underlineStyle ?? 0, underlineColorIndex: spec.underlineColorIndex ?? -1, underlineArgb: spec.underlineArgb ?? null } };
    });
    const lines = Array.from({ length: savedResetRows }, (_, row) => grid.slice(row * savedResetCols, (row + 1) * savedResetCols).map(spec => spec.char).join('').trimEnd());
    return { rows: savedResetRows, cols: savedResetCols, lines, cursorRow: cursor[0], cursorCol: cursor[1], cursorVisible: true,
        attributeRuns: coalesce(parts.map(part => part.attribute)), resolvedColors: coalesce(parts.map(part => part.resolved)),
        cellDetails: coalesce(parts.map(part => part.detail)), colorBinding: binding,
        bufferState: { scrollTop: 0, scrollBottom: savedResetRows - 1, originMode: false, autoWrap: true, insertMode: false, pendingWrap: false,
            alternateScreen: alt, scrollbackLines: history.length, ...buffer },
        history: { startLine: 0, totalLines: history.length, lines: [...history] } };
}

/** Placements of one text from a position; spaces leave the grid blank. */
export const put = (row, col, text, options = {}) => [...text].flatMap((char, offset) => (char === ' ' ? [] : [{ row, col: col + offset, spec: colorCell(char, options) }]));
const bold1 = { attrs: ['bold'], fg: paletteChannel(1) };
const under2 = { underlineStyle: 1, fg: paletteChannel(2) };
const styled = { attrs: ['bold', 'italic', 'inverse'] };
const fullX = { attrs: ['bold', 'dim', 'italic', 'inverse', 'hidden', 'strikethrough', 'blink', 'overline', 'underline'], underlineStyle: 1, underlineArgb: literalArgb({ r: 7, g: 8, b: 9 }),
    fg: literalChannel(1, 2, 3), bg: literalChannel(4, 5, 6) };

/** The screens of every read group of one synthetic case (a conclusive, supported run), keyed by role. */
export function syntheticSavedResetReads(caseId) {
    const blank = { cells: [], cursor: [0, 0] };
    const mainBlank = blank, altBlank = { ...blank, alt: true };
    const primary = (cursor = [4, 6]) => ({ cells: [...put(0, 0, 'PRIMARY-A'), ...put(2, 0, 'PRIMARY-B')], cursor });
    if (caseId === 'modes.alt-screen.exit') {
        const styledSeed = () => ({ cells: [...put(0, 0, 'STYLED-A', bold1), ...put(2, 0, 'STYLED-B', under2)], cursor: [4, 6] });
        return { initial: mainBlank, seed: primary(), entered: altBlank, marked: { cells: put(0, 0, 'ALT-MARK'), cursor: [0, 8], alt: true },
            exited: primary(), final: primary(), reentry: { cells: put(0, 0, 'ALT-MARK'), cursor: [0, 8], alt: true }, back: primary(),
            staleSeed: primary(), staleExited: primary(), styledSeed: styledSeed(), styledExited: styledSeed(),
            styledWitness: { cells: [...styledSeed().cells, ...put(4, 6, 'W', { attrs: ['bold'], fg: paletteChannel(4) })], cursor: [4, 7] } };
    }
    if (caseId === 'modes.altscreen-47' || caseId === 'modes.altscreen-1047') {
        const retains = caseId === 'modes.altscreen-47', main = retains ? 'MAIN-47' : 'MAIN-1047', mark = retains ? 'ALT-47' : 'ALT-1047';
        const seed = { cells: put(0, 0, main), cursor: [2, 4] };
        const reads = { initial: mainBlank, pinnedEntered: altBlank, pinnedExited: mainBlank, seed, active: altBlank,
            marked: { cells: put(1, 1, mark), cursor: [1, 1 + mark.length], alt: true }, exited: seed,
            reentry: retains ? { cells: put(1, 1, mark), cursor: [1, 1 + mark.length], alt: true } : altBlank, back: seed };
        if (!retains) {
            reads.preSeed = mainBlank;
            reads.retainedEntry = { cells: put(0, 0, 'PRE-ALT'), cursor: [0, 7], alt: true };
            reads.retainedExit = mainBlank;
        }
        return reads;
    }
    if (caseId === 'reset.sgr') {
        const pair = (x, y) => ({ cells: [...put(0, 0, 'X', x), ...put(0, 1, 'Y', y)], cursor: [0, 2] });
        return { initial: mainBlank, pinned: pair(styled, {}), persist: pair(styled, styled), full: pair(fullX, {}), omitted: pair(styled, {}) };
    }
    const grid = { cells: [], cursor: [0, 0], fill: colorCell('E') };
    return { initial: mainBlank, pinned: grid, seed: { cells: [...put(0, 0, 'ABABABABABAB'), ...put(1, 0, 'BABABABABABA'), ...put(2, 0, 'ABABABABABAB')], cursor: [0, 0] },
        aligned: grid, margins: { ...grid, buffer: { scrollTop: 2, scrollBottom: 5 } }, pen: grid };
}

/** The screens of every read group of one synthetic case, in plan order. */
export function syntheticSavedResetScreens(caseId, binding = paletteBinding(), readsOf = null) {
    const reads = readsOf?.(caseId) ?? syntheticSavedResetReads(caseId);
    const contract = savedResetContracts.find(item => item.caseId === caseId);
    return contract.roles.map(role => syntheticSavedResetScreen(reads[role], binding));
}

/** One journaled frame with its digest. */
const frame = (index, kind, screen, barrier) => ({ index, kind, paneId, barrier, screen, sha256: snapshotHash(screen) });

/** A complete, valid synthetic run: frames, worker run log and adapter facts. `readsOf(caseId)` may override the per-case reads. */
export function syntheticSavedResetEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), readsOf = null) {
    const nonce = 'NTILDE_SR_' + runId;
    const blank = syntheticSavedResetScreen({}, binding);
    const bound = { ...syntheticSavedResetScreen({}, binding), lines: [nonce, ...Array.from({ length: savedResetRows - 1 }, () => '')], cursorCol: nonce.length };
    const frames = [frame(0, 'svrs-start', blank, null), frame(1, 'svrs-bind', bound, deviceAttribute)];
    const cases = savedResetContracts.map(contract => {
        const feeds = planFeeds(contract);
        const barriers = feeds.map(() => deviceAttribute);
        const screens = syntheticSavedResetScreens(contract.caseId, binding, readsOf);
        for (const screen of screens) frames.push(frame(frames.length, 'svrs-read', screen, deviceAttribute));
        const pinned = { probeId: contract.caseId, ...replaySavedResetPinned(contract, screens) };
        return { caseId: contract.caseId, feeds, barriers, reads: screens.map(screen => ({ kind: 'cells', cursor: [screen.cursorRow, screen.cursorCol] })),
            pinnedFed: contract.pinnedFeeds.length, pinned };
    });
    frames.push(frame(frames.length, 'svrs-finish', blank, deviceAttribute));
    const run = { schemaVersion: 1, adapterId: savedResetAdapterId, probeIds: savedResetContracts.map(contract => contract.caseId), cases };
    const adapter = { id: savedResetAdapterId, derivedFromAdapterId: 'ntilde-owned-scrollback-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter };
}

/** The independent contract verdict for one synthetic case. */
export function syntheticSavedResetGrade(caseId, binding = paletteBinding(), readsOf = null) {
    const contract = savedResetContracts.find(item => item.caseId === caseId);
    return gradeSavedResetCase(contract, savedResetObservation(contract, syntheticSavedResetScreens(caseId, binding, readsOf)));
}
