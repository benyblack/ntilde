// Explicit fixture inputs. All writes stay inside the worker's already proved private PTY.
export const pixelSuiteId = 'ntilde-owned-render-pixels-v1';
export const pixelTimes = Object.freeze([0, 529, 530, 1059, 1060]);
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const reset = '\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H\x1b[?25l';
const colors = '\x1b[38;2;240;240;240;48;2;16;16;16m';
const text = 'HMWqg_|';
export const pixelRegion = Object.freeze({ row: 2, col: 4, rows: 1, cols: text.length });
export const blankRegion = Object.freeze({ row: 4, col: 4, rows: 1, cols: text.length });
export const controlRegion = Object.freeze({ row: 6, col: 4, rows: 1, cols: text.length });
export const cursorRegion = Object.freeze({ row: 8, col: 8, rows: 1, cols: 1 });
function input(sgr = '') {
    return reset + colors + at(2, 4) + sgr + text + '\x1b[0m' + colors + at(4, 4) + sgr + ' '.repeat(text.length)
        + '\x1b[0m' + colors + at(6, 4) + text + '\x1b[0m' + at(8, 8);
}
const scene = (id, sequence, times = [0]) => Object.freeze({ id, sequence, times: Object.freeze(times) });
function paletteInput() {
    let result = reset;
    for (let index = 0; index < 16; index++) {
        result += at(2, 2 + index * 2) + `\x1b[0;48;5;${index}m  `;
        result += at(4, 2 + index * 2) + `\x1b[0;38;5;${index};48;2;${index < 8 ? '255;255;255' : '0;0;0'}m\u2588\u2588`;
    }
    return result + at(6, 2) + '\x1b[0;48;2;23;91;173m    ' + '\x1b[0;38;2;23;91;173m\u2588\u2588\u2588\u2588'
        + '\x1b[0m' + at(8, 8);
}
export const pixelScenes = Object.freeze([
    scene('normal', input()),
    scene('colors', paletteInput()),
    ...[['bold', '1'], ['italic', '3'], ['faint', '2'], ['hidden', '8'], ['reverse', '7'],
        ['strike', '9'], ['overline', '53'], ...[1, 2, 3, 4, 5].map(style => [`underline.${style}`, `4:${style};58;2;32;224;96`])]
        .map(([id, sgr]) => scene(id, input(`\x1b[${sgr}m`))),
    scene('text-blink', input('\x1b[5;4;9;53m'), pixelTimes),
    ...['default', 0, 1, 2, 3, 4, 5, 6].map(parameter => scene(`cursor.${parameter}`,
        input() + (parameter === 'default' ? '' : `\x1b[${parameter} q`) + '\x1b[?25h', [0, 530])),
    scene('cursor.hidden', input() + '\x1b[1 q\x1b[?25l', [0, 530]),
]);
export const pixelPlan = Object.freeze(pixelScenes.flatMap(item => item.times.map(timeMs => Object.freeze({ sceneId: item.id, timeMs }))));

/** A phase change captures the same screen without rewriting it or advancing a wall clock. */
async function captureScene(item, fence, owner) {
    const ordering = await fence.write(item.sequence);
    // Every phase must finish before the next RPC; concurrent captures cannot qualify.
    return item.times.reduce((previous, timeMs) => previous.then(() =>
        owner('pixel-read', ordering, { sceneId: item.id, timeMs })), Promise.resolve());
}

export async function collectPixels({ config, fence, owner }) {
    let initial;
    try { initial = await owner('pixel-start', null, { timeMs: 0 }); }
    catch (error) {
        if (error.code === 'screenDetailsUnavailable') return { status: 'unavailable', reason: error.code };
        throw error;
    }
    // Pixel-only failures are fatal: absence or invalid metadata must never look like support.
    const { pristineCursorBaseline } = await import('./cursor.mjs');
    if (initial.screen.rows < 10 || !pristineCursorBaseline(initial.screen)) throw new Error('Pixel fixture requires a pristine owned baseline.');
    try {
        const barrier = await fence.write('\x1b[?1049h' + reset + `NTILDE_PX_${config.runId}`);
        await owner('pixel-bind', barrier, { timeMs: 0 });
        await pixelScenes.reduce((previous, item) => previous.then(() => captureScene(item, fence, owner)), Promise.resolve());
    } finally {
        await owner('pixel-finish', await fence.write('\x1b[?1049l\x1b[!p'), { timeMs: 0 });
    }
}
