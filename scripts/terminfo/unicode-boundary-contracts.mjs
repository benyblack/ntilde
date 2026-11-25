// These are the three remaining terminfo.dev@4.0.0 Unicode baseline probes.
import { isDeepStrictEqual as same } from 'node:util';
import { charsetsFlagBits } from './charsets-contracts.mjs';
import { clearRow, deriveWidth, home } from './unicode-width-contracts.mjs';

export const scope = Object.freeze({ id: 'ntilde-owned-unicode-boundaries-v1', persistence: 'Off', deadlineMs: 90000 });
export const prefix = 'uboundary-';
export const caseIds = Object.freeze(['unicode.east-asian-ambiguous', 'unicode.grapheme-cursor', 'unicode.wrap-boundary']);
export const ambiguous = '\u25cf';
export const family = '\u{1f468}\u200d\u{1f469}\u200d\u{1f467}';
export const cjk = '\u4e2d';
const clearScreen = home + '\x1b[2J';

export function requireBoundary(condition, reason) {
    if (!condition) throw new Error('Unicode boundaries: ' + reason);
}

export function boundaryPlan(cols) {
    requireBoundary(Number.isInteger(cols) && cols >= 4 && cols <= 256, 'unsupported column count');
    return [
        { stage: 'ambiguous-seed', bytes: clearRow + 'AQ' },
        { stage: 'ambiguous-origin', bytes: home },
        { stage: 'ambiguous-target', bytes: ambiguous + 'X', query: true },
        { stage: 'ambiguous-width', sample: ambiguous },
        { stage: 'grapheme-seed', bytes: clearRow + 'AB' },
        { stage: 'grapheme-origin', bytes: home },
        { stage: 'grapheme-target', bytes: family, query: true },
        { stage: 'grapheme-backspace', bytes: '\b', query: true },
        { stage: 'grapheme-width', sample: family },
        { stage: 'parser-wrap-clear', bytes: clearScreen },
        { stage: 'parser-wrap-seed', bytes: 'A'.repeat(cols) },
        { stage: 'parser-wrap-origin', bytes: `\x1b[1;${cols}H` },
        { stage: 'parser-wrap-target', bytes: cjk, query: true },
        { stage: 'terminal-wrap-clear', bytes: clearScreen },
        { stage: 'terminal-wrap-seed', bytes: 'A'.repeat(cols - 1) },
        { stage: 'terminal-wrap-target', bytes: cjk, query: true },
    ];
}

const { wide, continuation, extended } = charsetsFlagBits;
const structural = wide | continuation | extended;
function position(frame, row, col) {
    return same(frame.cursor, [row, col]);
}

function rowsMatch(frame, ...lines) {
    return frame.lines.every((line, index) => line === (lines[index] ?? ''));
}

function flagsMatch(frame, index = 0, expected = []) {
    return frame.flags.every((cell, col) => (cell.flags & structural) === (expected[col - index] ?? 0));
}

function seedThenHome(seed, origin, text) {
    return rowsMatch(seed, text) && position(seed, 0, 2) && flagsMatch(seed)
        && rowsMatch(origin, text) && position(origin, 0, 0) && flagsMatch(origin);
}

function verdict(valid, pass) {
    if (!valid) return 'inconclusive';
    return pass ? 'supported' : 'unsupported';
}

function ambiguousGrade(frames, widthFrames) {
    const seed = frames['ambiguous-seed'], origin = frames['ambiguous-origin'], target = frames['ambiguous-target'];
    const controls = seedThenHome(seed, origin, 'AQ');
    const width = deriveWidth(ambiguous, widthFrames['ambiguous-width']);
    const seenWidth = target.cursor[1] - 1;
    const acceptedWidth = seenWidth === 1 || seenWidth === 2;
    const flags = seenWidth === 2 ? [wide, continuation] : [];
    return { id: caseIds[0], terminal: verdict(width !== null, width === 1 || width === 2), ptyWidth: width,
        effect: verdict(controls, acceptedWidth && rowsMatch(target, ambiguous + 'X') && flagsMatch(target, 0, flags) && target.cursor[0] === 0),
        observed: { line: target.lines[0], cursor: target.cursor, flags: target.flags.slice(0, 4).map(cell => cell.flags) } };
}

function graphemeGrade(frames, widthFrames) {
    const seed = frames['grapheme-seed'], origin = frames['grapheme-origin'], target = frames['grapheme-target'];
    const backspace = frames['grapheme-backspace'], controls = seedThenHome(seed, origin, 'AB');
    const width = deriveWidth(family, widthFrames['grapheme-width']);
    const flags = [wide | extended, continuation];
    const stored = rowsMatch(target, family) && flagsMatch(target, 0, flags);
    const effect = verdict(controls, stored && position(target, 0, 2));
    return { id: caseIds[1], terminal: verdict(width !== null, width === 2), ptyWidth: width, effect,
        backspace: verdict(effect === 'supported', rowsMatch(backspace, family) && flagsMatch(backspace, 0, flags) && position(backspace, 0, 0)),
        observed: { line: target.lines[0], cursor: target.cursor, backspaceCursor: backspace.cursor } };
}

function wrapGrade(cols, frames, terminalReply) {
    const full = 'A'.repeat(cols), almost = 'A'.repeat(cols - 1);
    const start = frames['parser-wrap-seed'], origin = frames['parser-wrap-origin'], parser = frames['parser-wrap-target'];
    const termSeed = frames['terminal-wrap-seed'], terminal = frames['terminal-wrap-target'];
    const cleared = ['parser-wrap-clear', 'terminal-wrap-clear'].every(id => {
        const frame = frames[id];
        return rowsMatch(frame) && flagsMatch(frame) && position(frame, 0, 0);
    });
    const parserControls = rowsMatch(start, full) && rowsMatch(origin, full)
        && [start, origin].every(frame => position(frame, 0, cols - 1) && flagsMatch(frame))
        && start.pendingWrap && !origin.pendingWrap;
    const termControls = rowsMatch(termSeed, almost) && position(termSeed, 0, cols - 1) && flagsMatch(termSeed) && !termSeed.pendingWrap;
    const compatible = (target, seedText) => rowsMatch(target, seedText, cjk)
        && flagsMatch(target, cols, [wide, continuation]) && position(target, 1, 2) && !target.pendingWrap;
    return { id: caseIds[2], terminal: verdict(terminalReply !== null, terminalReply?.row === 2),
        effect: verdict(cleared && parserControls && termControls, compatible(parser, full) && compatible(terminal, almost)),
        observed: { parserCursor: parser.cursor, terminalCursor: terminal.cursor, terminalReply, parserRow0: parser.lines[0],
            terminalRow0: terminal.lines[0], parserRow1: parser.lines[1], terminalRow1: terminal.lines[1] } };
}

export function gradeBoundaries(cols, frames, widthFrames, terminalReply) {
    return [ambiguousGrade(frames, widthFrames), graphemeGrade(frames, widthFrames), wrapGrade(cols, frames, terminalReply)];
}
