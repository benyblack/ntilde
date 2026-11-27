import { isDeepStrictEqual as same } from 'node:util';
import { da, geometryQuery, leave, limit, paneArea, rawText } from './sixel-reports-contracts.mjs';

export { leave };
export const scope = Object.freeze({ id: 'ntilde-owned-xtwinops-pixels-v1', persistence: 'Off', deadlineMs: 90000 });
export const prefix = 'xtpixels-';
export const initialFontSize = 14;
export const caseIds = ['device.xtwinops-14', 'device.xtwinops-16'];
export const requests = ['\x1b[14t', '\x1b[16t'];
export const nonce = runId => 'NTILDE_XTPIX_' + runId;

export function requireGeometry(condition, detail) {
    if (!condition) throw new Error('XTWINOPS pixels: ' + detail);
}

function queryStages(geometry) {
    return [{ stage: geometry, geometry },
        ...[...requests, geometryQuery].map((query, index) => ({
            stage: geometry + '-' + index, geometry, sample: true, index, queries: [query + da],
        }))];
}

export function planFor(runId) {
    return [
        { stage: 'start' },
        { stage: 'bind', write: '\x1b[?1049h\x1b[2J\x1b[H' + nonce(runId) },
        ...queryStages('initial'),
        { stage: 'resize', resize: 'smaller' },
        ...queryStages('resized'),
        { stage: 'increase', fontDelta: 1 },
        ...queryStages('font-changed'),
        { stage: 'decrease', fontDelta: -1 },
        { stage: 'restore', resize: 'original' },
        ...queryStages('restored'),
        { stage: 'finish', write: leave },
        { stage: 'seal' },
    ];
}

// Convert each DIP dimension to the single-precision device cell metric before
// multiplying by the grid. Each reported dimension independently rounds to even.
function roundCell(dip, scale) {
    const pixels = Math.max(1, Math.fround(dip * scale));
    const floor = Math.floor(pixels);
    return pixels - floor === 0.5 ? floor + floor % 2 : Math.round(pixels);
}

export function pixelDimensions(cell, window) {
    const area = paneArea(cell);
    const cellPixels = { width: roundCell(cell.cellWidthDip, cell.renderScaling),
        height: roundCell(cell.cellHeightDip, cell.renderScaling) };
    const cappedArea = { width: Math.min(limit, area.width), height: Math.min(limit, area.height) };
    const outerWindow = { width: Math.round(window[4] * cell.backingScale),
        height: Math.round(window[5] * cell.backingScale) };
    requireGeometry(area.width !== area.height && cellPixels.width !== cellPixels.height && !same(area, outerWindow),
        'fixture does not distinguish swapped axes or the native outer window');
    return { area, cell: cellPixels, cappedArea, outerWindow };
}

function dimensions(match, widthFirst) {
    return match ? { width: Number(match[widthFirst ? 1 : 2]), height: Number(match[widthFirst ? 2 : 1]) } : null;
}

export function verifyReply(samples, step, used, expected) {
    requireGeometry(samples?.length === 1 && samples[0].requestBytes === step.queries[0], 'query bytes or order changed');
    const raw = rawText(samples[0].sample, used);
    const sentinel = /\x1b\[\?[0-9;]+c$/.exec(raw)?.[0];
    requireGeometry(sentinel, 'the pinned DA1 sentinel is missing');
    const reply = raw.slice(0, -sentinel.length);
    const area = step.index === 0, cell = step.index === 1;
    const pattern = area ? /^\x1b\[4;([1-9]\d*);([1-9]\d*)t$/
        : cell ? /^\x1b\[6;([1-9]\d*);([1-9]\d*)t$/ : /^\x1b\[\?2;0;([1-9]\d*);([1-9]\d*)S$/;
    const match = pattern.exec(reply);
    const observed = dimensions(match, !area && !cell);
    const target = area ? expected.area : cell ? expected.cell : expected.cappedArea;
    return {
        ...(step.index < 2 ? { id: caseIds[step.index] } : {}),
        request: step.queries[0], raw, reply, expected: target, observed,
        outcome: observed === null ? 'inconclusive' : same(observed, target) ? 'passed' : 'failed',
    };
}
