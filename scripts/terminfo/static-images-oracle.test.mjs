import test from 'node:test';
import assert from 'node:assert/strict';
import { staticImageInput } from './static-images-inputs.mjs';
import { inspectStaticImagePixels, staticImageEnvelope } from './static-images-oracle.mjs';

const grid = { originXPx: 2, originYPx: 4, cellWidthPx: 10, cellHeightPx: 18, renderWidthPx: 200, renderHeightPx: 160 };
const anchor = { col: 3, row: 4 };
function baseline() {
    const value = { width: 200, height: 160, pixels: Buffer.alloc(200 * 160 * 4) };
    for (let i = 0; i < value.pixels.length; i += 4) value.pixels.set([16, 16, 16, 255], i);
    return value;
}
function draw(sourceId, rect, { alpha = true } = {}) {
    const value = baseline(), raw = staticImageInput(sourceId, 'rgba').bytes;
    for (let y = 0; y < rect.height; y++) for (let x = 0; x < rect.width; x++) {
        const sx = Math.floor(x * 16 / rect.width), sy = Math.floor(y * 16 / rect.height);
        const pixel = [...raw.subarray((sy * 16 + sx) * 4, (sy * 16 + sx + 1) * 4)];
        if (!pixel[3] && alpha) continue;
        value.pixels.set([...pixel.slice(0, 3), 255], ((rect.y + y) * value.width + rect.x + x) * 4);
    }
    return value;
}
const inspect = (observed, options = {}) => inspectStaticImagePixels({ baseline: baseline(), observed, grid, anchor, geometry: 'aspect', ...options });
const all = items => items.every(item => item.passed);

for (const [geometry, sourceId, rect] of [
    ['native', 'opaque', { x: 32, y: 76, width: 16, height: 16 }],
    ['stretch', 'alpha', { x: 32, y: 76, width: 40, height: 36 }],
    ['aspect', 'alpha', { x: 34, y: 76, width: 36, height: 36 }],
    ['aspect', 'reversed', { x: 32, y: 76, width: 36, height: 36 }],
]) test(`synthetic ${geometry}/${sourceId} meets independent color, alpha, geometry and non-target checks only`, () => {
    const value = inspect(draw(sourceId, rect), { geometry, sourceId });
    assert.ok(all(value.controls)); assert.ok(all(value.assertions));
    assert.deepEqual(value.measurement.contentBounds, rect);
    assert.equal(Object.hasOwn(value, 'result'), false);
    assert.match(value.scope, /no ownership, live execution, freshness or stock grade/);
});

test('aspect oracle rejects cell-envelope stretching even when every color/alpha interior matches', () => {
    const value = inspect(draw('alpha', { x: 32, y: 76, width: 40, height: 36 }));
    assert.ok(all(value.controls)); assert.equal(value.assertions[0].passed, false);
    assert.ok(all(value.assertions.slice(1)));
});

test('native Sixel size and anchor are independent of rounded cell allocation', () => {
    for (const rect of [{ x: 32, y: 76, width: 20, height: 18 }, { x: 33, y: 76, width: 16, height: 16 }]) {
        const value = inspect(draw('opaque', rect), { geometry: 'native', sourceId: 'opaque' });
        assert.equal(value.assertions[0].passed, false);
    }
});

test('wrong decoded source, orientation and lost transparency fail pixel assertions despite valid placement', () => {
    const rect = { x: 32, y: 76, width: 36, height: 36 };
    const wrongSource = inspect(draw('reversed', rect));
    assert.equal(wrongSource.assertions[0].passed, true);
    assert.equal(all(wrongSource.assertions.slice(1)), false);
    const lostAlpha = inspect(draw('alpha', rect, { alpha: false }));
    assert.equal(lostAlpha.assertions.at(-1).passed, false);
});

test('no-display, disappearance and missing-placement stages require actual blank pixels, not an ACK', () => {
    assert.ok(all(inspect(baseline(), { visible: false }).assertions));
    assert.equal(inspect(draw('alpha', { x: 32, y: 76, width: 36, height: 36 }), { visible: false }).assertions[0].passed, false);
    assert.equal(inspect(baseline()).assertions[0].passed, false);
});

test('independent blank reference and full non-target stability are mandatory controls', () => {
    const observed = draw('alpha', { x: 32, y: 76, width: 36, height: 36 });
    observed.pixels.set([255, 0, 0, 255], 0);
    assert.equal(inspect(observed).controls[1].passed, false);
    const wrongReference = baseline(); wrongReference.pixels.set([255, 0, 0, 255], (76 * 200 + 32) * 4);
    const value = inspect(observed, { baseline: wrongReference });
    assert.equal(value.controls[0].passed, false);
});

test('actual grid geometry is required; invalid dimensions, types, allocations and source names fail closed', () => {
    const good = baseline();
    assert.deepEqual(staticImageEnvelope(good, grid, anchor), { x: 32, y: 76, width: 40, height: 36 });
    for (const options of [
        { observed: { ...good, pixels: Buffer.alloc(1) } },
        { observed: { width: 8192, height: 8192, pixels: Buffer.alloc(0) } },
        { grid: { ...grid, renderWidthPx: 199 } },
        { grid: { ...grid, cellWidthPx: 0 } },
        { grid: { ...grid, originXPx: -1 } },
        { anchor: { col: 1000, row: 4 } },
        { cells: { cols: 99, rows: 2 } },
        { sourceId: '__proto__' }, { geometry: 'renderer-stretch' }, { visible: 'true' },
    ]) assert.throws(() => inspect(good, options));
});
