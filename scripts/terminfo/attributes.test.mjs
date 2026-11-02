import assert from 'node:assert/strict';
import { test } from 'node:test';
import { copyAttributeRuns, expandAttributeRuns, readAttributeRuns } from './attributes.mjs';

test('lossless row-major runs preserve every flag and index across row boundaries', () => {
    const runs = readAttributeRuns(['0301:-1/-1|4200:255/-1|', '4200:255/-1|8100:-1/0|'], 2, 2);
    assert.deepEqual(runs, [{ count: 1, flags: 769, fg: -1, bg: -1 },
        { count: 2, flags: 16896, fg: 255, bg: -1 }, { count: 1, flags: 33024, fg: -1, bg: 0 }]);
    assert.deepEqual(expandAttributeRuns(runs, 4), [
        { flags: 769, fg: -1, bg: -1 }, { flags: 16896, fg: 255, bg: -1 },
        { flags: 16896, fg: 255, bg: -1 }, { flags: 33024, fg: -1, bg: 0 },
    ]);
    const copy = copyAttributeRuns(runs, 4); runs[0].flags = 0;
    assert.equal(copy[0].flags, 769);
});

for (const [name, lines, rows, cols] of [
    ['missing rows', null, 1, 1], ['missing columns', ['0300:-1/-1|'], 1, 2],
    ['extra columns', ['0300:-1/-1|0300:-1/-1|'], 1, 1], ['missing separator', ['0300:-1/-1'], 1, 1],
    ['oversized row', ['x'.repeat(15)], 1, 1], ['bad flags', ['300:-1/-1|'], 1, 1],
    ['invalid index', ['4200:256/-1|'], 1, 1], ['negative index', ['0300:-2/-1|'], 1, 1],
    ['noncanonical index', ['4200:01/-1|'], 1, 1], ['bad token', ['no attribute|'], 1, 1],
]) test('attribute read refuses ' + name, () => assert.throws(() => readAttributeRuns(lines, rows, cols)));

for (const [name, runs, cells] of [
    ['missing data', undefined, 1], ['empty data', [], 1],
    ['invalid count', [{ count: 0, flags: 768, fg: -1, bg: -1 }], 1],
    ['incomplete viewport', [{ count: 1, flags: 768, fg: -1, bg: -1 }], 2],
    ['overflow viewport', [{ count: 3, flags: 768, fg: -1, bg: -1 }], 2],
    ['invalid flags', [{ count: 1, flags: 65536, fg: -1, bg: -1 }], 1],
    ['invalid palette', [{ count: 1, flags: 768, fg: -1, bg: 256 }], 1],
    ['adjacent duplicate', [{ count: 1, flags: 768, fg: -1, bg: -1 }, { count: 1, flags: 768, fg: -1, bg: -1 }], 2],
]) test('retained runs refuse ' + name, () => assert.throws(() => copyAttributeRuns(runs, cells)));
