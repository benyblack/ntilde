// Live native-window evidence for the frozen Modern SGR 5 criterion.
import { createHash } from 'node:crypto';
import { isDeepStrictEqual as same } from 'node:util';
import { parseChecksumReportFrame } from './checksum-report.mjs';

export const blinkScope = Object.freeze({ id: 'owned-modern-blink-direct-v1', persistence: 'Off', deadlineMs: 90000 });
export const blinkPrefix = 'mblink';
export const blinkNonce = 'NTILDE_MBLINK_';
export const blinkFrames = 20;
export const blinkIntervalMs = 350;
export const targetRow = 3;
export const controlRow = 6;
export const textCol = 7;
export const caretRow = 11;

export const da = '\x1b[c';
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const park = at(caretRow, 0);
export const blinkBind = runId => '\x1b[?47h\x1b[0m\x1b[2J\x1b[H' + blinkNonce + runId;
export const blinkBlank = '\x1b[?25l' + park;
export const blinkPlain = at(targetRow, textCol) + 'X' + at(controlRow, textCol) + 'X' + park;
export const blinkStyled = at(targetRow, textCol) + '\x1b[5mX' + park;
export const blinkQuery = '\x1bP$qm\x1b\\';
export const blinkFinish = '\x1b[0m\x1b[2J\x1b[H\x1b[?25h\x1b[?47l';
export const blinkHash = bytes => createHash('sha256').update(bytes).digest('hex');
export const checkBlink = (condition, reason) => { if (!condition) throw new Error('Modern blink: ' + reason); };

export function parseBlinkReply(bytes) {
    const parsed = parseChecksumReportFrame(bytes);
    if (!parsed) return null;
    const response = parsed.reply?.raw ?? null;
    checkBlink(response === null || /^[01]\$r/.test(response.slice(2)), 'unexpected DECRQSS exchange bytes');
    return { response, barrier: parsed.barrier };
}

export function gradeBlink(frames, geometry) {
    const insufficient = reason => ({ conclusion: 'inconclusive', reason });
    if (!frames.length) return insufficient('no live captures');
    if (frames.some(frame => !same(frame.before, geometry) || !same(frame.after, geometry))) {
        return insufficient('native window identity or geometry changed');
    }
    if (frames.some(frame => frame.control !== frames[0].control)) return insufficient('unstyled control changed');
    if (frames.some((frame, index) => !Number.isFinite(frame.startMs) || !Number.isFinite(frame.endMs)
        || frame.startMs < 0 || frame.endMs < frame.startMs || (index && frame.startMs <= frames[index - 1].endMs))) {
        return insufficient('invalid monotonic capture timestamps');
    }
    // The compositor sample occurs inside each command's recorded interval.
    // Bound the elapsed time and gaps conservatively for a no-change verdict.
    const spanMs = Math.max(0, frames.at(-1).startMs - frames[0].endMs);
    const maxGapMs = Math.max(0, ...frames.slice(1).map((frame, index) => frame.endMs - frames[index].startMs));
    const timing = { frameCount: frames.length, spanMs, maxGapMs };
    const seen = new Map([[frames[0].styled, 0]]);
    let changedAt;
    for (let index = 1; index < frames.length; index++) {
        const { styled } = frames[index];
        if (styled === frames[index - 1].styled) continue;
        if (seen.has(styled)) {
            return { conclusion: 'pass', reason: 'styled crop changed and returned with a stable unstyled control', ...timing,
                returnFrames: [seen.get(styled), changedAt, index] };
        }
        seen.set(styled, index);
        changedAt = index;
    }
    if (changedAt !== undefined) return { ...insufficient('styled crop changed without a return'), ...timing };
    if (frames.length < 12 || spanMs < 6000 || maxGapMs > 545) {
        return { ...insufficient('capture too slow to decide blink'), ...timing };
    }
    return { conclusion: 'fail', reason: 'styled crop stayed static throughout the required sampling window', ...timing };
}

function changedRows(blank, plain) {
    const bands = [];
    let band;
    for (let y = 0; y < plain.height; y++) {
        let min = plain.width, max = -1;
        for (let x = 0; x < plain.width; x++) {
            const offset = (y * plain.width + x) * 4;
            if (blank.pixels.readUInt32BE(offset) !== plain.pixels.readUInt32BE(offset)) {
                min = Math.min(min, x);
                max = x;
            }
        }
        if (max < 0) continue;
        if (band && y - band.bottom <= 2) {
            band.left = Math.min(band.left, min);
            band.right = Math.max(band.right, max);
            band.bottom = y;
        } else {
            band = { left: min, right: max, top: y, bottom: y };
            bands.push(band);
        }
    }
    return bands;
}

// Calibrate while both X cells are plain; never derive crop geometry from a blink sample.
export function blinkCrops(blank, plain) {
    checkBlink(blank.width === plain.width && blank.height === plain.height, 'calibration image dimensions changed');
    const bands = changedRows(blank, plain);
    const [target, control] = bands;
    checkBlink(bands.length === 2 && Math.abs(target.left - control.left) <= 1
        && Math.abs(target.right - control.right) <= 1 && control.top - target.bottom >= 8, 'could not isolate the two calibration glyphs');
    const rectangle = ({ left, right, top, bottom }) => ({ x: left - 2, y: top - 2, width: right - left + 5, height: bottom - top + 5 });
    return { styled: rectangle(target), control: rectangle(control) };
}

export function blinkCropHash(image, { x, y, width, height }) {
    checkBlink(x >= 0 && y >= 0 && x + width <= image.width && y + height <= image.height, 'crop outside the native window');
    const digest = createHash('sha256');
    for (let row = y; row < y + height; row++) {
        const start = (row * image.width + x) * 4;
        digest.update(image.pixels.subarray(start, start + width * 4));
    }
    return digest.digest('hex');
}
