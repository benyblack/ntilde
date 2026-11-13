import assert from 'node:assert/strict';
import { test } from 'node:test';
import { replayInsertReplacePinned, replayInsertReplaceTerminal } from './insert-replace-pinned-replay.mjs';
import { insertReplacePinnedSeam } from './insert-replace-pinned.mjs';
import { charEditSeams } from './char-edit-grading-collector.mjs';

const valid = () => ({ cols: 8, rows: 3, seed: ['A', 'B', 'C', ' '],
    origin: { x: 0, y: 0, visible: true }, after: ['X', 'A', 'B', 'C'] });
const grade = input => replayInsertReplacePinned(input).observation.outcome;

test('pinned termless criterion distinguishes inserted, replaced and ambiguous cells', () => {
    assert.equal(grade(valid()), 'supported');
    assert.equal(grade({ ...valid(), after: ['X', 'B', 'C', ' '] }), 'unsupported');
    assert.equal(grade({ ...valid(), after: ['X', 'B', 'A', 'C'] }), 'inconclusive');
    assert.equal(grade({ ...valid(), seed: ['A', 'Z', 'C', ' '] }), 'inconclusive');
    assert.equal(grade({ ...valid(), origin: { x: 1, y: 0, visible: true } }), 'inconclusive');
    assert.equal(grade({ ...valid(), cols: 3 }), 'inconclusive');
});

test('the distinct terminal path retains its inconclusive result and exact writes', async () => {
    const helpers = charEditSeams.filter(seam => seam.startsWith('function isBlank(') || seam.startsWith('function parserStateResult('));
    const factory = new Function(`${helpers.join('\n')}\nconst probe = (id, termless, term) => ({ id, termless, term });\nreturn [{\n${insertReplacePinnedSeam}];`);
    const probe = factory()[0];
    for (const sample of [{ rows: 3, cols: 8, position: { row: 1, col: 3 } },
        { rows: 3, cols: 8, position: null }, { rows: 1, cols: 4, position: null }]) {
        const writes = [];
        const actual = await probe.term({ rows: sample.rows, cols: sample.cols, write: bytes => writes.push(bytes),
            queryCursorPosition: async () => sample.position });
        assert.deepEqual(actual, replayInsertReplaceTerminal(sample));
        assert.deepEqual(writes, sample.cols < 5 ? [] : ['\x1b[1;1H\x1b[2K', 'ABCD', '\x1b[1;2H', '\x1b[4h', 'X', '\x1b[4l']);
    }
    assert.throws(() => replayInsertReplaceTerminal({ rows: 3, cols: 8 }), /malformed/);
});

test('a missing or malformed sample cannot produce a conclusive verdict', () => {
    for (const edit of [{ seed: null }, { seed: ['A', 'B'] }, { origin: null },
        { after: null }, { after: ['X', 'A', 'B', 3] }]) {
        assert.throws(() => replayInsertReplacePinned({ ...valid(), ...edit }), /Missing or malformed/);
    }
});

test('independent replay matches the pinned probe for supported, unsupported and inconclusive samples', () => {
    const helpers = charEditSeams.filter(seam => seam.startsWith('function isBlank(') || seam.startsWith('function parserStateResult('));
    // Execute the retained upstream probe itself; the test adapter supplies only
    // journal-like observations and verifies each byte string requested by it.
    const factory = new Function(`${helpers.join('\n')}\nconst probe = (id, termless, term) => ({ id, termless, term });\nreturn [{\n${insertReplacePinnedSeam}];`);
    const probe = factory()[0];
    for (const sample of [valid(), { ...valid(), after: ['X', 'B', 'C', ' '] },
        { ...valid(), after: ['X', 'B', 'A', 'C'] }, { ...valid(), seed: ['A', 'Z', 'C', ' '] },
        { ...valid(), origin: { x: 1, y: 0, visible: true } }, { ...valid(), cols: 3 }]) {
        let feeds = 0;
        const exact = ['\x1b[4l\x1b[1;1H\x1b[2KABC', '\x1b[1;1H', '\x1b[4h', 'X', '\x1b[4l'];
        const ctx = { cols: sample.cols, getScrollback: () => ({ screenLines: sample.rows }),
            feed(bytes) { assert.equal(bytes, exact[feeds++]); }, getCursor: () => sample.origin,
            getCell: (_row, col) => ({ char: (feeds === 1 ? sample.seed : sample.after)[col] }) };
        assert.deepEqual(probe.termless(ctx), replayInsertReplacePinned(sample));
        assert.equal(feeds, sample.cols < 4 ? 0 : sample.seed[1] !== 'B' || sample.origin.x !== 0 ? 2 : 5);
    }
});
