import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { decodeCapturePng } from './pixels-png.mjs';
import { expectedStaticSourcePixel, staticImageInput, staticSixel, staticSixelSha256 } from './static-images-inputs.mjs';

for (const sourceId of ['opaque', 'alpha', 'reversed']) {
    test(`immutable ${sourceId} PNG and RGBA bytes match the independent asymmetric spatial definition`, () => {
        const png = staticImageInput(sourceId), raw = staticImageInput(sourceId, 'rgba');
        const image = decodeCapturePng(png.bytes);
        assert.equal(image.width, 16); assert.equal(image.height, 16);
        assert.deepEqual(image.pixels, raw.bytes);
        for (let y = 0; y < 16; y++) for (let x = 0; x < 16; x++) {
            assert.deepEqual([...image.pixels.subarray((y * 16 + x) * 4, (y * 16 + x + 1) * 4)], expectedStaticSourcePixel(sourceId, x, y));
        }
        for (const input of [png, raw]) assert.equal(createHash('sha256').update(input.bytes).digest('hex'), input.sha256);
        png.bytes.fill(0); raw.bytes.fill(0);
        assert.equal(staticImageInput(sourceId).bytes[0], 137);
        assert.equal(staticImageInput(sourceId, 'rgba').bytes.length, 1024);
    });
}

test('sources differ, invalid names and coordinates fail without exposing mutable source state', () => {
    assert.notEqual(staticImageInput('alpha').sha256, staticImageInput('reversed').sha256);
    for (const name of ['__proto__', 'constructor', '', null]) assert.throws(() => staticImageInput(name));
    assert.throws(() => staticImageInput('alpha', 'gif'));
    assert.throws(() => expectedStaticSourcePixel('alpha', 16, 0));
    assert.throws(() => expectedStaticSourcePixel('alpha', 0, -1));
});

test('fixed Sixel input has a 1:1 16x16 raster and independently reconstructs every opaque source pixel', () => {
    assert.equal(createHash('sha256').update(staticSixel).digest('hex'), staticSixelSha256);
    const prefix = '\x1bP0;1q"1;1;16;16';
    assert.ok(staticSixel.startsWith(prefix)); assert.ok(staticSixel.endsWith('\x1b\\'));
    let data = staticSixel.slice(prefix.length, -2);
    for (const definition of ['#0;2;100;0;0', '#1;2;0;100;0', '#2;2;0;0;100', '#3;2;100;100;0']) {
        assert.ok(data.startsWith(definition)); data = data.slice(definition.length);
    }
    const palette = [[255, 0, 0, 255], [0, 255, 0, 255], [0, 0, 255, 255], [255, 255, 0, 255]];
    const pixels = new Array(256); let x = 0, y = 0, color = 0;
    for (let i = 0; i < data.length; i++) {
        const c = data[i];
        if (c === '#') { color = Number(data[++i]); assert.ok(color >= 0 && color < 4); }
        else if (c === '$') x = 0;
        else if (c === '-') { x = 0; y += 6; }
        else {
            assert.ok(c.charCodeAt(0) >= 63 && c.charCodeAt(0) <= 126); assert.ok(x < 16);
            const bits = c.charCodeAt(0) - 63;
            for (let bit = 0; bit < 6; bit++) if (bits & (1 << bit)) {
                assert.ok(y + bit < 16); pixels[(y + bit) * 16 + x] = palette[color];
            }
            x++;
        }
    }
    for (let y = 0; y < 16; y++) for (let x = 0; x < 16; x++) assert.deepEqual(pixels[y * 16 + x], expectedStaticSourcePixel('opaque', x, y));
});

test('fixed SIXEL independently separates declared raster, painted pixels and six-band canvas', async () => {
    const { staticSixelCanvas } = await import('./static-images-inputs.mjs');
    assert.deepEqual(staticSixelCanvas(), { declaredWidth: 16, declaredHeight: 16, width: 16, height: 18, paintedWidth: 16, paintedHeight: 16 });
});
