// Theme-resolved color projection of the owned readScreen observer: per-cell
// resolved foreground/background RGB plus the palette/theme binding they were
// resolved against. Resolution itself belongs to the App's rendering layer; this
// module only validates what the App reported and enforces that every resolved
// value coheres with the binding and the stored attribute runs — arbitrary
// supplied RGB cannot pass validation.
import { createHash } from 'node:crypto';
import { expandScreenFlags } from './ascii-sgr-contracts.mjs';

const uint = value => Number.isSafeInteger(value) && value >= 0 && value <= 0xffffffff;
const index = value => Number.isSafeInteger(value) && value >= 0 && value <= 255;
export const resolvedColorSources = Object.freeze(['default', 'palette', 'literal']);

/** Validate and normalize one complete palette/theme binding exactly as the App reports it. */
export function copyColorBinding(binding) {
    if (!binding || binding.stage !== 'theme-resolution' || typeof binding.stageNote !== 'string'
        || binding.stageNote.length < 64 || binding.stageNote.length > 2048
        || (binding.themeName !== null && typeof binding.themeName !== 'string')
        || !uint(binding.foregroundArgb) || !uint(binding.backgroundArgb)
        || !Array.isArray(binding.ansiPaletteArgb) || binding.ansiPaletteArgb.length !== 256
        || !binding.ansiPaletteArgb.every(uint)
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
    return { stage: binding.stage, stageNote: binding.stageNote, themeName: binding.themeName ?? null,
        foregroundArgb: binding.foregroundArgb, backgroundArgb: binding.backgroundArgb,
        ansiPaletteArgb: [...binding.ansiPaletteArgb], reverseVideo: binding.reverseVideo,
        foregroundOverride: binding.foregroundOverride, backgroundOverride: binding.backgroundOverride,
        paletteOverrideIndices: [...binding.paletteOverrideIndices], minimumContrast: binding.minimumContrast ?? null };
}

/** The canonical digest of a normalized binding; theme changes during observation change it. */
export const colorBindingHash = binding => createHash('sha256')
    .update(JSON.stringify(copyColorBinding(binding))).digest('hex');

const channelValid = (channel, field, binding) => {
    if (!resolvedColorSources.includes(channel?.source) || !uint(channel.argb)) return false;
    if (channel.source === 'default') {
        return channel.index === -1 && channel.argb === binding[field];
    }
    if (channel.source === 'palette') {
        return index(channel.index) && channel.argb === binding.ansiPaletteArgb[channel.index];
    }
    return channel.index === -1; // literal: exact stored bytes, opaque at this stage
};

const sameRun = (a, b) => a.fg.source === b.fg.source && a.fg.index === b.fg.index && a.fg.argb === b.fg.argb
    && a.bg.source === b.bg.source && a.bg.index === b.bg.index && a.bg.argb === b.bg.argb;

/** Validate canonical, complete resolved-color runs cohering with the binding. */
export function copyResolvedColors(runs, cells, binding) {
    if (!Number.isSafeInteger(cells) || cells < 1 || cells > 8192
        || !Array.isArray(runs) || !runs.length || runs.length > Math.min(cells, 1024)) {
        throw new Error('Invalid resolved-color budget.');
    }
    let total = 0, previous;
    const result = runs.map(run => {
        if (!run || !Number.isSafeInteger(run.count) || run.count < 1
            || !channelValid(run.fg, 'foregroundArgb', binding) || !channelValid(run.bg, 'backgroundArgb', binding)) {
            throw new Error('Invalid resolved-color run.');
        }
        const value = { count: run.count, fg: { ...run.fg }, bg: { ...run.bg } };
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
        .flatMap(({ count, ...value }) => Array.from({ length: count }, () => ({ fg: { ...value.fg }, bg: { ...value.bg } })));
}

/** The pinned projection of one channel: default is null (defined, distinguishing it
 * from unexposed), palette and literal resolve to exposed {r,g,b} values. */
export const exposedRgb = channel => channel.source === 'default'
    ? null : { r: (channel.argb >> 16) & 255, g: (channel.argb >> 8) & 255, b: channel.argb & 255 };

/** Require the resolved channels to agree with the stored attribute runs cell by cell:
 * default markers, palette flags with exact indices, and literal storage may never be
 * confused with one another. This is the anti-substitution binding between the two
 * projections of the same coherent snapshot. */
export function checkStoredColorCoherence(screen) {
    const cells = screen.rows * screen.cols;
    const flags = expandScreenFlags(screen);
    const resolved = expandResolvedColors(screen.resolvedColors, cells, screen.colorBinding);
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
        for (const [label, channel, defaultFlag, indexFlag, indexField] of [['fg', cell.fg, 256, 16384, 'fg'], ['bg', cell.bg, 512, 32768, 'bg']]) {
            const isDefault = (value & defaultFlag) !== 0 && (value & indexFlag) === 0;
            const isPalette = (value & indexFlag) !== 0;
            if (isDefault !== (channel.source === 'default') || isPalette !== (channel.source === 'palette')) {
                throw new Error(`Resolved ${label} source disagrees with stored attributes at ${row},${col}.`);
            }
            if (isPalette && channel.index !== (run?.[indexField] ?? -1)) {
                throw new Error(`Resolved ${label} index disagrees with stored attributes at ${row},${col}.`);
            }
        }
    }
}
