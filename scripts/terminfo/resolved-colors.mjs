// Theme-resolved color projection of the owned readScreen observer: per-cell
// resolved foreground/background RGB plus the palette/theme binding they were
// resolved against. Resolution itself belongs to the App's rendering layer; this
// module only validates what the App reported and enforces that every resolved
// value coheres with the binding and the stored attribute runs — arbitrary
// supplied RGB cannot pass validation.
import { createHash } from 'node:crypto';
import { expandScreenFlags } from './ascii-sgr-contracts.mjs';
import { expandCellDetails } from './cell-details.mjs';

/** An unsigned 32-bit ARGB integer. */
const uint = value => Number.isSafeInteger(value) && value >= 0 && value <= 0xffffffff;
/** A palette index 0..255. */
const index = value => Number.isSafeInteger(value) && value >= 0 && value <= 255;
/** The resolution stage is opaque: every reported color must carry 0xFF alpha. */
const opaque = value => uint(value) && (value >>> 24) === 0xff;
/** The three storage states a resolved channel can be resolved from. */
export const resolvedColorSources = Object.freeze(['default', 'palette', 'literal']);

/** The opt-in underline stage marker and its note, both required together. */
function validUnderlineStage(binding) {
    if (binding.underlineStage !== 'underline-resolution' || typeof binding.underlineStageNote !== 'string'
        || binding.underlineStageNote.length < 64 || binding.underlineStageNote.length > 2048) {
        throw new Error('Invalid resolved-color underline stage.');
    }
    return { underlineStage: binding.underlineStage, underlineStageNote: binding.underlineStageNote };
}

/** Validate and normalize one complete palette/theme binding exactly as the App reports it. */
export function copyColorBinding(binding) {
    if (!binding || binding.stage !== 'theme-resolution' || typeof binding.stageNote !== 'string'
        || binding.stageNote.length < 64 || binding.stageNote.length > 2048
        || (binding.themeName !== null && typeof binding.themeName !== 'string')
        || !opaque(binding.foregroundArgb) || !opaque(binding.backgroundArgb)
        || !Array.isArray(binding.ansiPaletteArgb) || binding.ansiPaletteArgb.length !== 256
        || !binding.ansiPaletteArgb.every(opaque)
        || typeof binding.reverseVideo !== 'boolean' || typeof binding.foregroundOverride !== 'boolean'
        || typeof binding.backgroundOverride !== 'boolean'
        || !Array.isArray(binding.paletteOverrideIndices)
        || binding.paletteOverrideIndices.some((value, position) => !index(value)
            || (position > 0 && value <= binding.paletteOverrideIndices[position - 1]))
        || binding.paletteOverrideIndices.length > 256
        || (binding.minimumContrast !== null && binding.minimumContrast !== undefined
            && !(typeof binding.minimumContrast === 'number' && Number.isFinite(binding.minimumContrast)
                && binding.minimumContrast >= 1 && binding.minimumContrast <= 21))) {
        throw new Error('Invalid resolved-color theme binding.');
    }
    // The underline stage is an additive opt-in: absent, the normalized shape (and its hash) is exactly the frozen one.
    const underline = binding.underlineStage === undefined && binding.underlineStageNote === undefined ? null
        : validUnderlineStage(binding);
    return { ...(underline ?? {}), stage: binding.stage, stageNote: binding.stageNote, themeName: binding.themeName ?? null,
        foregroundArgb: binding.foregroundArgb, backgroundArgb: binding.backgroundArgb,
        ansiPaletteArgb: [...binding.ansiPaletteArgb], reverseVideo: binding.reverseVideo,
        foregroundOverride: binding.foregroundOverride, backgroundOverride: binding.backgroundOverride,
        paletteOverrideIndices: [...binding.paletteOverrideIndices], minimumContrast: binding.minimumContrast ?? null };
}

/** The canonical digest of a normalized binding; theme changes during observation change it. */
export const colorBindingHash = binding => createHash('sha256')
    .update(JSON.stringify(copyColorBinding(binding))).digest('hex');

/** One channel is valid only in its own source's shape and exactly at its binding value. */
const channelValid = (channel, field, binding) => {
    if (!resolvedColorSources.includes(channel?.source) || !opaque(channel.argb)) return false;
    if (channel.source === 'default') {
        return channel.index === -1 && channel.argb === binding[field];
    }
    if (channel.source === 'palette') {
        return index(channel.index) && channel.argb === binding.ansiPaletteArgb[channel.index];
    }
    return channel.index === -1; // literal: exact stored bytes, opaque at this stage
};

/** The four sources a resolved underline can come from. */
export const resolvedUnderlineSources = Object.freeze(['none', 'inherit', 'palette', 'literal']);

/** One underline is valid only in its own source's shape; palette values sit exactly at the binding's entry. Inherit's
 * value is checked against the cell's own channel by `checkUnderlineCoherence`, because it is not a binding constant. */
const underlineValid = (ul, binding) => {
    if (!ul || !resolvedUnderlineSources.includes(ul.source)) return false;
    if (ul.source === 'none') return ul.index === -1 && ul.argb === undefined && ul.inheritsFrom === undefined;
    if (!opaque(ul.argb)) return false;
    if (ul.source === 'inherit') return ul.index === -1 && (ul.inheritsFrom === 'foreground' || ul.inheritsFrom === 'background');
    if (ul.inheritsFrom !== undefined) return false;
    if (ul.source === 'palette') return index(ul.index) && ul.argb === binding.ansiPaletteArgb[ul.index];
    return ul.index === -1; // literal
};
const copyUnderline = ul => ({ source: ul.source, index: ul.index,
    ...(ul.inheritsFrom === undefined ? {} : { inheritsFrom: ul.inheritsFrom }), ...(ul.argb === undefined ? {} : { argb: ul.argb }) });
const sameUnderline = (a, b) => a === undefined || b === undefined ? a === b
    : a.source === b.source && a.index === b.index && a.inheritsFrom === b.inheritsFrom && a.argb === b.argb;

/** Full-run equality (both channels, source, index and value, and the underline when it is projected). */
const sameRun = (a, b) => a.fg.source === b.fg.source && a.fg.index === b.fg.index && a.fg.argb === b.fg.argb
    && a.bg.source === b.bg.source && a.bg.index === b.bg.index && a.bg.argb === b.bg.argb
    && sameUnderline(a.ul, b.ul);

/** Validate canonical, complete resolved-color runs cohering with the binding. */
export function copyResolvedColors(runs, cells, binding) {
    if (!Number.isSafeInteger(cells) || cells < 1 || cells > 8192
        || !Array.isArray(runs) || !runs.length || runs.length > Math.min(cells, 1024)) {
        throw new Error('Invalid resolved-color budget.');
    }
    let total = 0, previous;
    const withUnderline = binding.underlineStage !== undefined;
    const result = runs.map(run => {
        if (!run || !Number.isSafeInteger(run.count) || run.count < 1
            || !channelValid(run.fg, 'foregroundArgb', binding) || !channelValid(run.bg, 'backgroundArgb', binding)) {
            throw new Error('Invalid resolved-color run.');
        }
        // A binding that carries the underline stage requires every run to project an underline; one without it forbids it,
        // so an opted-out read can never smuggle App values in and an opted-in read can never omit them.
        if (withUnderline ? !underlineValid(run.ul, binding) : run.ul !== undefined) throw new Error('Invalid resolved-color run.');
        const value = { count: run.count, fg: { ...run.fg }, bg: { ...run.bg }, ...(withUnderline ? { ul: copyUnderline(run.ul) } : {}) };
        total += run.count;
        if (total > cells || (previous && sameRun(previous, value))) throw new Error('Noncanonical resolved-color viewport.');
        previous = value;
        return value;
    });
    if (total !== cells) throw new Error('Incomplete resolved-color viewport.');
    return result;
}

/** Expand validated runs into independent per-cell channel pairs. */
export function expandResolvedColors(runs, cells, binding) {
    return copyResolvedColors(runs, cells, binding)
        .flatMap(({ count, ...value }) => Array.from({ length: count }, () => ({ fg: { ...value.fg }, bg: { ...value.bg },
            ...(value.ul === undefined ? {} : { ul: { ...value.ul } }) })));
}

/** The pinned projection of one channel: default is null (defined, distinguishing it
 * from unexposed), palette and literal resolve to exposed {r,g,b} values. */
export const exposedRgb = channel => channel.source === 'default'
    ? null : { r: (channel.argb >> 16) & 255, g: (channel.argb >> 8) & 255, b: channel.argb & 255 };

/** Require the resolved channels to agree with the stored projections of the same
 * snapshot cell by cell: default markers, palette flags with exact indices, and —
 * through the stored literal ARGB bytes carried by cellDetails — literal storage
 * resolved to exactly its stored RGB. This is the anti-substitution binding between
 * the projections; a screen without stored literal bytes cannot be validated.
 * `includeOverline` (default false, the frozen behavior) expands the stored details with their
 * overline state, so runs that differ only in overline are not mistaken for duplicates. */
export function checkStoredColorCoherence(screen, includeOverline = false) {
    const cells = screen.rows * screen.cols;
    const flags = expandScreenFlags(screen);
    const resolved = expandResolvedColors(screen.resolvedColors, cells, screen.colorBinding);
    const stored = expandCellDetails(screen.cellDetails, cells, includeOverline);
    const runAt = (row, col) => {
        const target = row * screen.cols + col;
        let offset = 0;
        for (const run of screen.attributeRuns ?? []) {
            if (target < offset + run.count) return run;
            offset += run.count;
        }
        return undefined;
    };
    for (const [position, cell] of resolved.entries()) {
        const value = flags[position] ?? 0;
        const row = Math.floor(position / screen.cols), col = position % screen.cols;
        const run = runAt(row, col);
        const detail = stored[position];
        for (const [label, channel, defaultFlag, indexFlag, indexField, literalField] of [
            ['fg', cell.fg, 256, 16384, 'fg', 'foregroundArgb'],
            ['bg', cell.bg, 512, 32768, 'bg', 'backgroundArgb'],
        ]) {
            const isDefault = (value & defaultFlag) !== 0 && (value & indexFlag) === 0;
            const isPalette = (value & indexFlag) !== 0;
            if (isDefault !== (channel.source === 'default') || isPalette !== (channel.source === 'palette')) {
                throw new Error(`Resolved ${label} source disagrees with stored attributes at ${row},${col}.`);
            }
            if (isPalette && channel.index !== (run?.[indexField] ?? -1)) {
                throw new Error(`Resolved ${label} index disagrees with stored attributes at ${row},${col}.`);
            }
            if (channel.source === 'literal' && detail[literalField] == null) {
                throw new Error(`Resolved literal ${label} has no stored bytes at ${row},${col}.`);
            }
            if (channel.source === 'literal' && (channel.argb & 0xffffff) !== (detail[literalField] & 0xffffff)) {
                throw new Error(`Resolved literal ${label} disagrees with stored bytes at ${row},${col}.`);
            }
        }
    }
}

/** Opt-in underline coherence: the App-resolved underline of every cell must agree with that same cell's stored
 * projections (underline flag, stored palette index / literal bytes / default marker, inverse flag) and, for an
 * inherited underline, with the cell's own resolved text channel. A parent-derived value cannot pass for an
 * observation: the check compares only what the App reported. */
export function checkUnderlineCoherence(screen) {
    if (screen.colorBinding?.underlineStage === undefined) throw new Error('Resolved underline stage was not requested.');
    const cells = screen.rows * screen.cols;
    const resolved = expandResolvedColors(screen.resolvedColors, cells, screen.colorBinding);
    const stored = expandCellDetails(screen.cellDetails, cells, true);
    for (const [position, cell] of resolved.entries()) {
        const detail = stored[position], ul = cell.ul;
        const where = `${Math.floor(position / screen.cols)},${position % screen.cols}`;
        const underlined = detail.underlineStyle !== 0;
        if ((ul.source !== 'none') !== underlined) throw new Error(`Resolved underline presence disagrees with the stored underline at ${where}.`);
        if (ul.source === 'palette' && (detail.underlineColorIndex !== ul.index || detail.underlineArgb != null)) {
            throw new Error(`Resolved underline palette index disagrees with stored attributes at ${where}.`);
        }
        if (ul.source === 'literal' && (detail.underlineColorIndex !== -1 || detail.underlineArgb == null
            || (detail.underlineArgb & 0xffffff) !== (ul.argb & 0xffffff))) {
            throw new Error(`Resolved literal underline disagrees with stored bytes at ${where}.`);
        }
        if (ul.source === 'inherit') {
            const inverse = (detail.flags & 2) !== 0;
            const channel = inverse ? cell.bg : cell.fg;
            if (detail.underlineColorIndex !== -1 || detail.underlineArgb != null
                || ul.inheritsFrom !== (inverse ? 'background' : 'foreground') || ul.argb !== channel.argb) {
                throw new Error(`Resolved inherited underline disagrees with the stored marker or the cell's text channel at ${where}.`);
            }
        }
    }
}
