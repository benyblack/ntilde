// Stored ARGB and underline attributes only. Theme/palette resolution belongs to
// the renderer and is deliberately absent from this lossless buffer projection.
const uint = value => Number.isSafeInteger(value) && value >= 0 && value <= 0xffffffff;
const index = value => Number.isSafeInteger(value) && value >= -1 && value <= 255;
const same = (a, b) => Object.keys(a).every(key => key === 'count' || a[key] === b[key]);

function colorValid(flags, defaultFlag, paletteFlag, paletteIndex, argb) {
    if (!index(paletteIndex)) return false;
    const inherited = (flags & defaultFlag) !== 0, palette = (flags & paletteFlag) !== 0;
    if (inherited && palette) return false;
    if (palette) return paletteIndex >= 0 && argb == null;
    return paletteIndex === -1 && (inherited ? argb == null : uint(argb));
}

function copyRun(run) {
    if (!run || !Number.isSafeInteger(run.count) || run.count < 1
        || !Number.isSafeInteger(run.flags) || run.flags < 0 || run.flags > 65535 || (run.flags & 1024)
        || !colorValid(run.flags, 256, 16384, run.fg, run.foregroundArgb)
        || !colorValid(run.flags, 512, 32768, run.bg, run.backgroundArgb)
        || !Number.isSafeInteger(run.underlineStyle) || run.underlineStyle < 0 || run.underlineStyle > 5
        || Boolean(run.flags & 8) !== Boolean(run.underlineStyle)
        || !index(run.underlineColorIndex)
        || (run.underlineArgb != null && (!uint(run.underlineArgb) || run.underlineColorIndex !== -1))
        || (!run.underlineStyle && (run.underlineColorIndex !== -1 || run.underlineArgb != null))) {
        throw new Error('Invalid stored cell detail run.');
    }
    return { count: run.count, flags: run.flags, fg: run.fg, bg: run.bg,
        foregroundArgb: run.foregroundArgb ?? null, backgroundArgb: run.backgroundArgb ?? null,
        underlineStyle: run.underlineStyle, underlineColorIndex: run.underlineColorIndex, underlineArgb: run.underlineArgb ?? null };
}

export function copyCellDetails(runs, cells) {
    if (!Number.isSafeInteger(cells) || cells < 1 || cells > 8192
        || !Array.isArray(runs) || !runs.length || runs.length > Math.min(cells, 1024)) throw new Error('Invalid cell detail budget.');
    let total = 0, previous;
    const result = runs.map(value => {
        const run = copyRun(value);
        total += run.count;
        if (total > cells || (previous && same(previous, run))) throw new Error('Noncanonical cell detail viewport.');
        previous = run;
        return run;
    });
    if (total !== cells) throw new Error('Incomplete cell detail viewport.');
    return result;
}

export function expandCellDetails(runs, cells) {
    return copyCellDetails(runs, cells).flatMap(({ count, ...value }) => Array.from({ length: count }, () => ({ ...value })));
}
