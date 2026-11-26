import { staticImageEnvelope } from './static-images-oracle.mjs';

export const scope = Object.freeze({ id: 'ntilde-owned-osc66-v1', persistence: 'Off', deadlineMs: 90000 });
export const prefix = 'osc66-';
export const da = '\x1b[c';
export const nonce = runId => 'NTILDE_OSC66_' + runId;
const osc66 = (option, payload) => '\x1b]66;' + option + ';' + payload + '\x07';
const anchor = Object.freeze({ col: 4, row: 4 });
const cup = (row, col) => '\x1b[' + (row + 1) + ';' + (col + 1) + 'H';
const target = cup(anchor.row, anchor.col);
const eraseTarget = cup(anchor.row + 1, anchor.col) + '\x1b[4X' + target + '\x1b[4X';

export function requireOsc66(condition, reason) {
    if (!condition) throw new Error('OSC 66: ' + reason);
}

export function planFor(runId, cursorVisible) {
    return [
        { stage: 'start', bytes: null, pixel: true },
        { stage: 'bind', bytes: '\x1b[?1049h\x1b[2J\x1b[H' + nonce(runId) },
        { stage: 'home', bytes: '\x1b[1;1H\x1b[2K\r', query: true },
        { stage: 'width-cpr', bytes: osc66('w=2', ' '), query: true },
        { stage: 'scale-cpr', bytes: osc66('s=2', ' '), query: true },
        { stage: 'blank', bytes: '\x1b[?25l\x1b[2J' + cup(4, 2) + '|' + cup(4, 10) + 'KEEP' + cup(6, 4) + 'KEEP' + target, pixel: true },
        { stage: 'ordinary-wide', bytes: target + 'MMMM', pixel: true },
        { stage: 'width-ink', bytes: eraseTarget + osc66('w=2', 'MMMM'), pixel: true },
        { stage: 'ordinary', bytes: eraseTarget + 'M', pixel: true },
        { stage: 'scale-ink', bytes: eraseTarget + osc66('s=2', 'M'), pixel: true },
        { stage: 'cleared', bytes: eraseTarget, pixel: true },
        { stage: 'finish', bytes: '\x1b[?1049l\x1b[?25' + (cursorVisible ? 'h' : 'l'), pixel: true },
    ];
}

function measureRow(before, after, envelope, y, background, facts) {
    const rowInTarget = y >= envelope.y && y < envelope.y + envelope.height;
    for (let x = 0; x < before.width; x++) {
        const offset = (y * before.width + x) * 4;
        const a = before.pixels.readUInt32BE(offset), b = after.pixels.readUInt32BE(offset);
        const inside = rowInTarget && x >= envelope.x && x < envelope.x + envelope.width;
        if (inside && a !== background) facts.blank = false;
        if (a === b) continue;
        if (!inside) { facts.outside++; continue; }
        facts.count++;
        facts.left = Math.min(facts.left, x);
        facts.top = Math.min(facts.top, y);
        facts.right = Math.max(facts.right, x);
        facts.bottom = Math.max(facts.bottom, y);
    }
}

export function measureInk(before, after, grid, cells, background) {
    const envelope = staticImageEnvelope(before, grid, anchor, cells);
    requireOsc66(before.width === after.width && before.height === after.height, 'render dimensions changed');
    const facts = { left: Infinity, top: Infinity, right: -1, bottom: -1, count: 0, outside: 0, blank: true };
    for (let y = 0; y < before.height; y++) measureRow(before, after, envelope, y, background, facts);
    const { count, outside, blank, left, top, right, bottom } = facts;
    return { envelope, count, outside, blank, bounds: count ? { x: left, y: top, width: right - left + 1, height: bottom - top + 1 } : null };
}

export function gradeInk(views, grid, backgroundArgb) {
    const background = ((backgroundArgb & 0xffffff) * 256) + 255;
    const bounds = { 'ordinary-wide': [4, 1], 'width-ink': [2, 1], ordinary: [1, 1], 'scale-ink': [2, 2] };
    const measurements = Object.fromEntries(Object.entries(bounds).map(([stage, [cols, rows]]) =>
        [stage, measureInk(views.blank, views[stage], grid, { cols, rows }, background)]));
    const { 'ordinary-wide': wide, 'width-ink': width, ordinary, 'scale-ink': scale } = measurements;
    const controls = [ordinary, wide].every(item => item.blank && item.outside === 0 && item.count > 0)
        && wide.bounds.x + wide.bounds.width > width.envelope.x + width.envelope.width;
    requireOsc66(controls && width.blank && scale.blank, 'ordinary ink or blank control failed');
    const widthPassed = width.outside === 0 && width.count > 0 && width.bounds.width < wide.bounds.width;
    const split = scale.envelope.y + grid.cellHeightPx, scaled = scale.bounds;
    const scalePassed = scale.outside === 0 && scaled !== null && scaled.y < split && scaled.y + scaled.height > split
        && scaled.height > ordinary.bounds.height && scaled.width > ordinary.bounds.width;
    return {
        width: { outcome: widthPassed ? 'supported' : 'unsupported', measurement: width, ordinary: wide },
        scale: { outcome: scalePassed ? 'supported' : 'unsupported', measurement: scale, ordinary },
    };
}
