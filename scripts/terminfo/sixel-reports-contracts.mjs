import { isDeepStrictEqual as same } from 'node:util';
import { validateInputBytes } from './input-state.mjs';

export const scope = Object.freeze({ id: 'ntilde-owned-sixel-reports-v1', persistence: 'Off', deadlineMs: 90000 });
export const prefix = 'sxreport-';
export const caseIds = Object.freeze(['extensions.sixel-da1', 'extensions.sixel-geometry-report']);
export const da = '\x1b[c';
export const geometryQuery = '\x1b[?2;1;0S';
export const paneQuery = '\x1b[14t';
export const queryWrites = Object.freeze([da, geometryQuery + da, paneQuery]);
export const nonce = runId => 'NTILDE_SIXEL_' + runId;
export const limit = 2000;
export const leave = '\x1b[?1049l';
const daFrame = /\x1b\[\?(\d+(?:;\d+)*);?c/;
const geometryFrame = /\x1b\[\?2;[0-9;]*S/;
const paneFrame = /^\x1b\[4;([1-9]\d{0,4});([1-9]\d{0,4})t$/;

export function requireSixel(condition, detail) {
    if (!condition) throw new Error('Sixel reports: ' + detail);
}

export function planFor(runId) {
    return [
        { stage: 'start' },
        { stage: 'bind', write: '\x1b[?1049h\x1b[2J\x1b[H' + nonce(runId) },
        { stage: 'initial', sample: true },
        { stage: 'resize', resize: 'smaller' },
        { stage: 'resized', sample: true },
        { stage: 'restore', resize: 'original' },
        { stage: 'restored', sample: true },
        { stage: 'finish', write: leave },
        { stage: 'seal' },
    ];
}

export function gridOf(value) {
    return { rows: value.rows, cols: value.cols };
}
const scaleOf = cell => [cell.cellWidthDip, cell.cellHeightDip, cell.renderScaling, cell.backingScale];
export const sameScale = (a, b) => same(scaleOf(a), scaleOf(b));

export function smallerGrid(cell) {
    const { viewRows: rows, viewCols: cols } = cell;
    const width = cell.cellWidthDip * cell.renderScaling, height = cell.cellHeightDip * cell.renderScaling;
    // screenSnapshot requires 48 columns, which also keeps the pane nonce intact.
    const minimumCols = 48;
    const target = { rows: Math.min(Math.floor(rows * 0.65), Math.floor(1900 / height)),
        cols: Math.min(Math.max(minimumCols, Math.floor(cols * 0.6)), Math.floor(1900 / width)) };
    requireSixel(target.rows >= 8 && target.cols >= minimumCols && target.rows < rows && target.cols < cols, 'bounded smaller grid unavailable');
    return target;
}

// App geometry provides logical cell metrics and device scaling independently of the PTY replies.
function areaAxis(cells, dip, scale) {
    const total = Math.fround(cells * Math.max(1, Math.fround(dip * scale)));
    const floor = Math.floor(total);
    const rounded = total - floor === 0.5 ? floor + (floor % 2) : Math.round(total);
    return Math.max(1, rounded);
}

export function paneArea(cell) {
    return { width: areaAxis(cell.viewCols, cell.cellWidthDip, cell.renderScaling),
        height: areaAxis(cell.viewRows, cell.cellHeightDip, cell.renderScaling) };
}

function rawText(capture, used) {
    validateInputBytes(capture, null, used, false);
    const raw = Buffer.from(capture.hex, 'hex');
    requireSixel(raw.every(byte => byte <= 127), 'non-ASCII query reply');
    return raw.toString('ascii');
}

export function verifyControl(capture, used) {
    requireSixel(rawText(capture, used) === '', 'unexpected reply to a fixture write');
}

function trace(sequence, raw, match, reason) {
    return { sequence, raw, match: match ? Array.from(match) : null, reason,
        rawBase64: Buffer.from(raw, 'ascii').toString('base64') };
}

export function verifySample(captures, used, cell) {
    requireSixel(Array.isArray(captures) && captures.length === queryWrites.length, 'incomplete query stage');
    const [primary, graphics, pane] = captures.map((capture, index) => {
        requireSixel(capture.requestBytes === queryWrites[index], 'query bytes or order changed');
        return rawText(capture.sample, used);
    });
    const primaryMatch = daFrame.exec(primary);
    requireSixel(primaryMatch?.[0] === primary && graphics.endsWith(primary), 'DA1 or geometry sentinel was missing or inconsistent');
    const reply = graphics.slice(0, -primary.length), graphicsMatch = geometryFrame.exec(reply);
    requireSixel(reply === '' || graphicsMatch?.[0] === reply, 'unexpected geometry reply bytes');
    const windowMatch = paneFrame.exec(pane);
    requireSixel(windowMatch?.[0] === pane, 'missing or malformed text-area control reply');
    const reference = { width: Number(windowMatch[2]), height: Number(windowMatch[1]) };
    const expectedArea = paneArea(cell);
    requireSixel(same(reference, expectedArea), 'text-area control disagrees with independently observed App geometry');
    const expected = { width: Math.min(limit, reference.width), height: Math.min(limit, reference.height) };
    const match = /^\x1b\[\?2;0;([1-9]\d{0,4});([1-9]\d{0,4})S$/.exec(reply);
    const observed = match ? { width: Number(match[1]), height: Number(match[2]) } : null;
    let outcome = same(observed, expected) ? 'passed' : 'failed';
    if (observed === null) outcome = 'inconclusive';
    return {
        pinnedExchanges: [trace(da, primary, primaryMatch, 'reply'),
            trace(geometryQuery + da, graphics, graphicsMatch, graphicsMatch ? 'reply' : 'sentinel')],
        geometry: { grid: { rows: cell.viewRows, cols: cell.viewCols }, cell, reference: { request: paneQuery, reply: pane, ...reference },
            expected, observed, outcome },
    };
}
