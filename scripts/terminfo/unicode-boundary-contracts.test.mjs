import assert from 'node:assert/strict';
import test from 'node:test';
import { charsetsFlagBits } from './charsets-contracts.mjs';
import { ambiguous, boundaryPlan, caseIds, cjk, family, gradeBoundaries } from './unicode-boundary-contracts.mjs';
import { widthWrites } from './unicode-width-contracts.mjs';

const { wide, continuation, extended } = charsetsFlagBits;
const cols = 8;
const cursor = (row, col) => ({ raw: `\x1b[${row};${col}R`, row, col });
const screen = (lines, row, col, occupied = {}, pendingWrap = false) => ({
    lines: [...lines, ...Array(3 - lines.length).fill('')],
    cursor: [row, col], pendingWrap,
    flags: Array.from({ length: cols * 3 }, (_, index) => ({ flags: occupied[index] ?? 0 })),
});
function exchanges(sample, width) {
    const replies = [cursor(2, 1), cursor(1, 2), cursor(1, width + 1), cursor(1, width + 2)];
    return widthWrites(sample).map((bytes, index) => ({ bytes, exchange: { barrier: '\x1b[?1;2c', reply: replies[index] } }));
}

function fixture(width = 1) {
    const a = 'A'.repeat(cols), tail = a.slice(1);
    const frames = {
        'ambiguous-seed': screen(['AQ'], 0, 2),
        'ambiguous-origin': screen(['AQ'], 0, 0),
        'ambiguous-target': screen([ambiguous + 'X'], 0, width + 1, width === 2 ? { 0: wide, 1: continuation } : {}),
        'grapheme-seed': screen(['AB'], 0, 2),
        'grapheme-origin': screen(['AB'], 0, 0),
        'grapheme-target': screen([family], 0, 2, { 0: wide | extended, 1: continuation }),
        'grapheme-backspace': screen([family], 0, 0, { 0: wide | extended, 1: continuation }),
        'parser-wrap-clear': screen([], 0, 0),
        'parser-wrap-seed': screen([a], 0, cols - 1, {}, true),
        'parser-wrap-origin': screen([a], 0, cols - 1),
        'parser-wrap-target': screen([a, cjk], 1, 2, { [cols]: wide, [cols + 1]: continuation }),
        'terminal-wrap-clear': screen([], 0, 0),
        'terminal-wrap-seed': screen([tail], 0, cols - 1),
        'terminal-wrap-target': screen([tail, cjk], 1, 2, { [cols]: wide, [cols + 1]: continuation }),
    };
    return { frames, widths: { 'ambiguous-width': exchanges(ambiguous, width), 'grapheme-width': exchanges(family, 2) } };
}

test('pinned fixtures preserve the distinct full-row parser and one-column-left PTY wrap', () => {
    const plan = Object.fromEntries(boundaryPlan(cols).map(step => [step.stage, step]));
    assert.equal(plan['ambiguous-seed'].bytes, '\x1b[1;1H\x1b[2KAQ');
    assert.equal(plan['ambiguous-target'].bytes, '●X');
    assert.equal(plan['grapheme-target'].bytes, '👨‍👩‍👧');
    assert.equal(plan['parser-wrap-seed'].bytes, 'A'.repeat(cols));
    assert.equal(plan['parser-wrap-origin'].bytes, '\x1b[1;8H');
    assert.equal(plan['terminal-wrap-seed'].bytes, 'A'.repeat(cols - 1));
    assert.equal(plan['terminal-wrap-target'].bytes, '中');
    assert.throws(() => boundaryPlan(3), /column count/);
});

test('both ambiguous widths are admissible when physical cells and cursor agree', () => {
    for (const width of [1, 2]) {
        const { frames, widths } = fixture(width);
        const grades = gradeBoundaries(cols, frames, widths, cursor(2, 3));
        assert.deepEqual(grades.map(grade => grade.id), caseIds);
        assert.deepEqual(grades.map(grade => grade.effect), Array(3).fill('supported'));
        assert.deepEqual(grades.map(grade => grade.terminal), Array(3).fill('supported'));
        assert.equal(grades[0].ptyWidth, width);
        assert.equal(grades[1].backspace, 'supported');
    }
});

test('a passing CPR alone cannot substitute for the right cluster or wrapped cells', () => {
    const { frames, widths } = fixture();
    frames['ambiguous-target'].lines[0] = 'AX';
    frames['grapheme-target'].lines[0] = '👨';
    frames['parser-wrap-target'].lines[1] = '';
    const grades = gradeBoundaries(cols, frames, widths, cursor(2, 3));
    assert.deepEqual(grades.map(grade => grade.effect), Array(3).fill('unsupported'));
    assert.deepEqual(grades.map(grade => grade.terminal), Array(3).fill('supported'));
});

test('a stale wrap plan or failed width helper leaves the corresponding grade inconclusive', () => {
    const { frames, widths } = fixture();
    frames['parser-wrap-origin'].pendingWrap = true;
    widths['ambiguous-width'][0].exchange.reply = null;
    const grades = gradeBoundaries(cols, frames, widths, null);
    assert.equal(grades[0].terminal, 'inconclusive');
    assert.equal(grades[2].terminal, 'inconclusive');
    assert.equal(grades[2].effect, 'inconclusive');
});
