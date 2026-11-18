import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { crc32, deflateSync } from 'node:zlib';
import { decodeCapturePng } from './pixels-png.mjs';

function chunk(type, data) {
    const result = Buffer.alloc(data.length + 12);
    result.writeUInt32BE(data.length);
    result.write(type, 4);
    data.copy(result, 8);
    result.writeUInt32BE(crc32(result.subarray(4, result.length - 4)), result.length - 4);
    return result;
}
function png(raw, { width = 2, height = 2, color = 2, suffix = Buffer.alloc(0) } = {}) {
    const header = Buffer.alloc(13);
    header.writeUInt32BE(width); header.writeUInt32BE(height, 4); header[8] = 8; header[9] = color;
    return Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]), chunk('IHDR', header),
        chunk('IDAT', Buffer.concat([deflateSync(Buffer.from(raw)), suffix])), chunk('IEND', Buffer.alloc(0))]);
}
const expected = Buffer.from([10,20,30,255,40,50,60,255,70,80,90,255,100,110,120,255]);
for (const [filter, raw] of [
    [0, [0,10,20,30,40,50,60, 0,70,80,90,100,110,120]],
    [1, [1,10,20,30,30,30,30, 1,70,80,90,30,30,30]],
    [2, [2,10,20,30,40,50,60, 2,60,60,60,60,60,60]],
    [3, [3,10,20,30,35,40,45, 3,65,70,75,45,45,45]],
    [4, [4,10,20,30,30,30,30, 4,60,60,60,30,30,30]],
]) test('RGB filter ' + filter, () => assert.deepEqual(decodeCapturePng(png(raw)).pixels, expected));

// The decoder reconstructs each filter type in a loop of its own. This checks it byte for byte against the
// specification's one-predictor-per-byte form, on rows wide and tall enough to reach every neighbour, for both
// channel counts and every filter type on a first row and on later rows.
function referencePixels(raw, width, height, channels) {
    const bytes = Buffer.from(raw), stride = width * channels, pixels = Buffer.alloc(width * height * 4);
    for (let y = 0; y < height; y++) {
        const start = y * (stride + 1) + 1, filter = bytes[start - 1];
        for (let x = 0; x < stride; x++) {
            const left = x >= channels ? bytes[start + x - channels] : 0;
            const up = y > 0 ? bytes[start + x - stride - 1] : 0;
            const upperLeft = y > 0 && x >= channels ? bytes[start + x - stride - 1 - channels] : 0;
            const estimate = left + up - upperLeft;
            const a = Math.abs(estimate - left), b = Math.abs(estimate - up), c = Math.abs(estimate - upperLeft);
            const predicted = [0, left, up, Math.floor((left + up) / 2), a <= b && a <= c ? left : b <= c ? up : upperLeft][filter];
            bytes[start + x] = (bytes[start + x] + predicted) & 255;
        }
        for (let x = 0; x < width; x++) {
            for (let k = 0; k < 4; k++) pixels[(y * width + x) * 4 + k] = k < channels ? bytes[start + x * channels + k] : 255;
        }
    }
    return pixels;
}
for (const [color, channels] of [[2, 3], [6, 4]]) {
    test(`every filter type on every row position matches the specification's form (${channels} channels)`, () => {
        const width = 7, height = 11;
        let seed = 0x9e3779b9 + channels;
        const next = () => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed >>> 24; };
        // Rows 0-4 put each type on the first row's successors in turn, and the rotation puts each type first too.
        for (let rotation = 0; rotation < 5; rotation++) {
            const raw = [];
            for (let y = 0; y < height; y++) {
                raw.push((y + rotation) % 5);
                for (let x = 0; x < width * channels; x++) raw.push(next());
            }
            assert.deepEqual(decodeCapturePng(png(raw, { width, height, color })).pixels, referencePixels(raw, width, height, channels));
        }
    });
}

test('RGBA keeps alpha', () => assert.deepEqual(decodeCapturePng(png([0,3,4,5,6],
    { width:1, height:1, color:6 })).pixels, Buffer.from([3,4,5,6])));
test('rejects CRC corruption', () => {
    const bytes = png([0,3,4,5,6], { width:1, height:1, color:6 });
    bytes[bytes.length - 1] ^= 1; assert.throws(() => decodeCapturePng(bytes));
});
test('rejects truncation', () => assert.throws(() => decodeCapturePng(png([]).subarray(0,40))));
test('rejects excessive geometry', () => assert.throws(() => decodeCapturePng(png([], { width:8192,height:8192 }))));
test('rejects invalid filter', () => assert.throws(() => decodeCapturePng(png([5,3,4,5,6], { width:1,height:1,color:6 }))));
test('rejects excess inflated bytes', () => assert.throws(() => decodeCapturePng(png([0,3,4,5,6,7], { width:1,height:1,color:6 }))));
test('rejects trailing compressed bytes', () => assert.throws(() => decodeCapturePng(png([0,3,4,5,6],
    { width:1,height:1,color:6,suffix:Buffer.from('junk') }))));

const one = () => png([0,3,4,5,6], { width:1,height:1,color:6 });
test('rejects trailing PNG bytes', () => assert.throws(() => decodeCapturePng(Buffer.concat([one(),Buffer.from([0])]))));
test('rejects a missing IEND', () => assert.throws(() => decodeCapturePng(one().subarray(0,-12))));
test('rejects unknown critical chunks', () => {
    const bytes = one();
    assert.throws(() => decodeCapturePng(Buffer.concat([bytes.subarray(0,33),chunk('ABCD',Buffer.alloc(0)),bytes.subarray(33)])));
});
test('rejects a set chunk reserved bit', () => {
    const bytes = one();
    assert.throws(() => decodeCapturePng(Buffer.concat([bytes.subarray(0,33),chunk('abca',Buffer.alloc(0)),bytes.subarray(33)])));
});
test('rejects animation rather than grading its poster frame', () => {
    const bytes = one();
    assert.throws(() => decodeCapturePng(Buffer.concat([bytes.subarray(0,33),chunk('acTL',Buffer.alloc(8)),bytes.subarray(33)])));
});
test('permits consecutive split IDATs', () => {
    const bytes = one(), compressed = deflateSync(Buffer.from([0,3,4,5,6]));
    const split = Buffer.concat([bytes.subarray(0,33),chunk('IDAT',compressed.subarray(0,4)),
        chunk('IDAT',compressed.subarray(4)),chunk('IEND',Buffer.alloc(0))]);
    assert.deepEqual(decodeCapturePng(split).pixels,Buffer.from([3,4,5,6]));
});
test('rejects nonconsecutive IDATs', () => {
    const bytes = one(), compressed = deflateSync(Buffer.from([0,3,4,5,6]));
    const split = Buffer.concat([bytes.subarray(0,33),chunk('IDAT',compressed.subarray(0,4)),chunk('tEXt',Buffer.from('a\0b')),
        chunk('IDAT',compressed.subarray(4)),chunk('IEND',Buffer.alloc(0))]);
    assert.throws(() => decodeCapturePng(split));
});

for (const name of ['BlockAndShadePrimitives', 'BoxDrawingGridPrimitives', 'CursorSelectionOverlay', 'SeamRegressionSurface', 'SgrBackgroundInverseRegions']) {
    test('decodes checked-in Skia PNG ' + name, () => {
        const path = new URL('../../tests/Ntilde.App.Tests/Baselines/Golden/shared/' + name + '.png', import.meta.url);
        const image = decodeCapturePng(readFileSync(path));
        assert.ok(image.width > 0 && image.height > 0);
        assert.equal(image.pixels.length, image.width * image.height * 4);
    });
}

test('rejects RGB transparency keys instead of silently making matching pixels opaque', () => {
    const bytes = png([0,3,4,5], { width:1, height:1, color:2 });
    const keyed = Buffer.concat([bytes.subarray(0,33), chunk('tRNS', Buffer.from([0,3,0,4,0,5])), bytes.subarray(33)]);
    assert.throws(() => decodeCapturePng(keyed));
});
