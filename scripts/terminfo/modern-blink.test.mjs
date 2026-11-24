import assert from 'node:assert/strict';
import test from 'node:test';
import { blinkCropHash, blinkCrops, gradeBlink, parseBlinkReply } from './modern-blink.mjs';

const window = [9, 42, 10, 20, 200, 120];
const sample = (index, styled = 'A', control = 'X') =>
    ({ startMs: index * 500, endMs: index * 500 + 20, before: window, after: window, styled, control });
const staticFrames = () => Array.from({ length: 14 }, (_, index) => sample(index));

test('an earlier styled appearance returns after an intervening change', () => {
    const frames = ['A', 'B', 'B', 'C', 'B'].map((appearance, index) => sample(index, appearance));
    assert.deepEqual(gradeBlink(frames, window), {
        conclusion: 'pass', reason: 'styled crop changed and returned with a stable unstyled control',
        frameCount: 5, spanMs: 1980, maxGapMs: 520, returnFrames: [1, 3, 4],
    });
    assert.match(gradeBlink(frames.slice(0, -1), window).reason, /changed without a return/);
});

test('a static series fails when the measured timing window is complete', () => {
    const frames = staticFrames();
    assert.deepEqual(gradeBlink(frames, window), {
        conclusion: 'fail', reason: 'styled crop stayed static throughout the required sampling window',
        frameCount: 14, spanMs: 6480, maxGapMs: 520,
    });
    frames[2].endMs += 25;
    assert.equal(gradeBlink(frames, window).conclusion, 'fail', '545 ms is inclusive');
});

test('a slow or incomplete static series is inconclusive with the frozen reason', () => {
    const frames = staticFrames();
    frames[2].endMs += 25.01;
    for (const series of [frames, staticFrames().slice(0, 11), staticFrames().slice(0, 13)]) {
        const grade = gradeBlink(series, window);
        assert.equal(grade.conclusion, 'inconclusive');
        assert.equal(grade.reason, 'capture too slow to decide blink');
    }
    const sparse = [sample(0), sample(7, 'B'), sample(14)];
    assert.equal(gradeBlink(sparse, window).conclusion, 'pass');
});

test('a changing control or moving window invalidates a styled return', () => {
    const frames = [sample(0), sample(1, 'B'), sample(2)];
    assert.deepEqual(gradeBlink([frames[0], { ...frames[1], control: 'Y' }, frames[2]], window),
        { conclusion: 'inconclusive', reason: 'unstyled control changed' });
    assert.deepEqual(gradeBlink([frames[0], frames[1], { ...frames[2], after: [9, 42, 11, 20, 200, 120] }], window),
        { conclusion: 'inconclusive', reason: 'native window identity or geometry changed' });
    assert.equal(gradeBlink([frames[0], { ...frames[1], startMs: 19 }, frames[2]], window).reason, 'invalid monotonic capture timestamps');
});

test('the two plain glyphs locate independent fixed crops', () => {
    const blank = { width: 30, height: 40, pixels: Buffer.alloc(30 * 40 * 4) };
    const plain = { ...blank, pixels: Buffer.from(blank.pixels) };
    for (const row of [7, 8, 24, 25]) plain.pixels.writeUInt32BE(0xffffffff, (row * 30 + 5) * 4);
    const crops = blinkCrops(blank, plain);
    assert.deepEqual(crops, { styled: { x: 3, y: 5, width: 5, height: 6 }, control: { x: 3, y: 22, width: 5, height: 6 } });
    assert.notEqual(blinkCropHash(blank, crops.styled), blinkCropHash(plain, crops.styled));
    assert.equal(blinkCropHash(plain, crops.styled), blinkCropHash(plain, crops.control));
    plain.pixels[33 * 30 * 4] = 1;
    assert.throws(() => blinkCrops(blank, plain), /isolate the two calibration glyphs/);
});

test('DECRQSS records an exact reply or fenced silence', () => {
    const barrier = '\x1b[?1;2c', response = '\x1bP1$r0;5m\x1b\\';
    assert.deepEqual(parseBlinkReply(response + barrier), { response, barrier });
    assert.deepEqual(parseBlinkReply(barrier), { response: null, barrier });
    assert.equal(parseBlinkReply(response), null);
    assert.throws(() => parseBlinkReply('junk' + barrier), /Unexpected rectangle checksum barrier bytes/);
});
