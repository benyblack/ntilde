// Fixtures from terminfo.dev@4.0.0, bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6.
import { isDeepStrictEqual as same } from 'node:util';
import { expandCellDetails } from './cell-details.mjs';
import { charsetsFlagBits } from './charsets-contracts.mjs';
import { validateCursorReport } from './cursor-report.mjs';

export const scope = Object.freeze({ id: 'ntilde-owned-unicode-width-v1', persistence: 'Off', deadlineMs: 90000 });
export const contractsVersion = 1;
export const prefix = 'uwidth-';
export const da = '\x1b[c';
export const home = '\x1b[1;1H';
export const clearRow = home + '\x1b[2K';
export const leave = '\x1b[?1049l';
export const nonce = runId => 'UW-' + runId;
const guard = 'KEEP';
const reset = clearRow + '\x1b[2;1H\x1b[2K' + guard + home;
export const enter = runId => '\x1b[?1049h\x1b[2J\x1b[H' + nonce(runId);
const specimens = [
    ['text.combining', 'e\u0301', 1, 'AB', 'X'],
    ['text.wide.emoji', '\u{1f389}', 2, 'AA', ''],
    ['text.wide.emoji-flags', '\u{1f1fa}\u{1f1f8}', 2, 'AA', 'X'],
    ['text.wide.emoji-vs16', '\u263a\ufe0f', 2, 'AA', 'X'],
    ['text.wide.emoji-zwj', '\u{1f468}\u200d\u{1f469}\u200d\u{1f467}', 2, 'AA', 'X'],
];
export const cases = Object.freeze(specimens.map(([id, sample, width, seed, suffix]) =>
    Object.freeze({ id, sample, width, seed, suffix })));

export const caseIds = Object.freeze(cases.map(item => item.id));

export function requireWidth(condition, description) {
    if (!condition) throw new Error('Unicode width: ' + description);
}
export function steps(runId) {
    return [
        { stage: 'start', bytes: null },
        { stage: 'bind', bytes: enter(runId) },
        ...cases.flatMap(item => [
            { stage: 'seed', item, bytes: reset + item.seed },
            { stage: 'clear', item, bytes: clearRow },
            { stage: 'sample', item, bytes: item.sample + item.suffix },
            { stage: 'width', item, bytes: null },
        ]),
        { stage: 'finish', bytes: leave },
    ];
}

// The pinned PTY helper checks addressing, a known ASCII advance, the sample,
// and an ASCII advance immediately after it. The runner adds a DA fence to
// each CPR exchange and retains the original CPR bytes.
export function widthWrites(sample) {
    return ['\x1b7\x1b[2;1H', home + 'A', home + sample, 'A'];
}
export const restoreWidth = '\x1b8';

function positionIs(report, row, col) {
    return report?.row === row && report.col === col;
}
export function deriveWidth(sample, frames) {
    const writes = widthWrites(sample);
    requireWidth(Array.isArray(frames) && frames.length === writes.length, 'missing width exchanges');
    const [second, ascii, sampleEnd, next] = frames.map(({ bytes, exchange }, index) => {
        requireWidth(bytes === writes[index], 'width fixture bytes differ');
        return validateCursorReport(exchange).reply;
    });
    if (!positionIs(second, 2, 1) || !positionIs(ascii, 1, 2) || sampleEnd?.row !== 1 || sampleEnd.col <= 1
        || !positionIs(next, 1, sampleEnd.col + 1)) return null;
    return sampleEnd.col - 1;
}

const { wide, continuation, extended, defaultColors } = charsetsFlagBits;
const structuralMask = wide | continuation | extended;
const plain = cell => (cell.flags & ~structuralMask) === defaultColors && cell.fg === -1 && cell.bg === -1
    && cell.foregroundArgb == null && cell.backgroundArgb == null && cell.underlineStyle === 0
    && cell.underlineColorIndex === -1 && cell.underlineArgb == null;

function matchesCells(cells, structuralFlags) {
    return cells.every((cell, index) => plain(cell)
        && (cell.flags & structuralMask) === (structuralFlags[index] ?? 0));
}

export function describeFrame(screen) {
    const flags = expandCellDetails(screen.cellDetails, screen.rows * screen.cols);
    return { lines: screen.lines, flags, cursor: [screen.cursorRow, screen.cursorCol] };
}

function controlled(frame, text, col, flags = []) {
    return frame.lines[0] === text && frame.lines[1] === guard && frame.lines.slice(2).every(line => line === '')
        && same(frame.cursor, [0, col]) && matchesCells(frame.flags, flags);
}

function verdict(ready, matches) {
    if (!ready) return 'inconclusive';
    return matches ? 'supported' : 'unsupported';
}

/** The stored row, the physical flags and the following cell are independent
 * observations. Width alone can't show that the full grapheme was retained. */
export function gradeCase(item, seed, blank, target, measurements) {
    // Every sample needs extended storage, including the single supplementary
    // scalar U+1F389, because the packed cell stores one UTF-16 code unit.
    const first = (item.width === 2 ? wide : 0) | extended;
    const expectedFlags = item.width === 2 ? [first, continuation] : [first];
    const controls = controlled(seed, item.seed, 2) && controlled(blank, '', 0);
    const effect = verdict(controls, controlled(target, item.sample + item.suffix, item.width + item.suffix.length, expectedFlags));
    const calibration = item.width === 1 ? 'A' : 'AA';
    const asciiWidth = deriveWidth(calibration, measurements.ascii);
    const sampleWidth = deriveWidth(item.sample, measurements.sample);
    const terminal = verdict(asciiWidth === calibration.length && sampleWidth !== null, sampleWidth === item.width);
    return { id: item.id, sample: item.sample, utf8Hex: Buffer.from(item.sample).toString('hex'),
        effect, terminal, asciiWidth, sampleWidth, controls, observed: { row: target.lines[0], cursor: target.cursor,
            flags: target.flags.slice(0, 6).map(cell => cell.flags) } };
}
