import test from 'node:test';
import assert from 'node:assert/strict';
import { staticImageInput } from './static-images-inputs.mjs';
import * as oracleApi from './static-images-oracle.mjs';
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
const inspect = (observed, options = {}) => inspectStaticImagePixels({ baseline: baseline(), observed, grid, anchor, geometry: 'aspect', ...(options.geometry === 'native' ? { imageDetails: calibration(10, 18) } : {}), ...options });
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
    for (const rect of [{ x: 32, y: 76, width: 20, height: 18 }, { x: 35, y: 76, width: 16, height: 16 }]) {
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

// Independent analytic calibration fixtures; never fit the observed colored bounds.
function calibration(cw = 20, ch = 36) {
    return { schemaVersion: 1, complete: true, totalPlacements: 1, placements: [{ index: 0,
        placementId: '11111111-2222-3333-4444-555555555555', col: 3, viewportRow: 4,
        widthCells: Math.ceil(Math.fround(16 / cw)), heightCells: Math.ceil(Math.fround(18 / ch)),
        clipTopRows: 0, clipBottomRows: 0, clipRightColumns: 0, preserveAspectRatio: false,
        bitmapWidthPx: 16, bitmapHeightPx: 18, sixelPlacement: { reservationWidthPx: 16, reservationHeightPx: 18,
            dimensionOrigin: 'sixel-geometry', cellWidthPx: cw, cellHeightPx: ch,
            cellWidthOrigin: 'sixel-override', cellHeightOrigin: 'sixel-override', arithmetic: 'float32-ceiling-v1' } }] };
}
function projectSixel(scaleX, scaleY) {
    const image = baseline(), raw = staticImageInput('opaque', 'rgba').bytes;
    for (let y = 0; y < image.height; y++) for (let x = 0; x < image.width; x++) {
        const u = (x + 0.5 - 32) / scaleX, v = (y + 0.5 - 76) / scaleY;
        if (u < 0 || v < 0 || u >= 16 || v >= 16) continue;
        const at = (Math.floor(v) * 16 + Math.floor(u)) * 4;
        image.pixels.set(raw.subarray(at, at + 4), (y * image.width + x) * 4);
    }
    return image;
}
test('calibrated fractional native pixels qualify without a16x16 capture-pixel assumption', () => {
    const value = inspect(projectSixel(0.8, 0.8), { geometry: 'native', sourceId: 'opaque', imageDetails: calibration(12.5, 22.5) });
    assert.ok(all(value.assertions)); assert.equal(value.qualification, null);
    assert.deepEqual(value.measurement.nativeProjection, { x: 32, y: 76, width: 12.8, height: 14.4, paintedWidth: 12.8, paintedHeight: 12.8 });
});
test('calibrated whole-cell compatibility cannot turn a native stretch negative into a pass', () => {
    const value = inspect(projectSixel(20 / 16, 18 / 18), { geometry: 'native', sourceId: 'opaque', imageDetails: calibration(10, 18) });
    assert.equal(value.measurement.compatibility.passed, true);
    assert.equal(value.assertions[0].passed, false); assert.equal(value.qualification, null);
});
test('null and nonphysical parse provenance are explicitly unqualified, never a native positive', () => {
    for (const origin of [null, 'parser-metric', 'default-fallback']) {
        const details = calibration(10, 18);
        if (origin === null) details.placements[0].sixelPlacement = null;
        else details.placements[0].sixelPlacement.cellWidthOrigin = origin;
        const value = inspect(projectSixel(1, 1), { geometry: 'native', sourceId: 'opaque', imageDetails: details });
        assert.match(value.qualification, /provenance|physical/); assert.equal(value.assertions[0].passed, null);
    }
});

test('too few safe color interiors at half-scale cannot qualify through the edge band', () => {
    const value = inspect(projectSixel(0.5, 0.5), { geometry: 'native', sourceId: 'opaque', imageDetails: calibration() });
    assert.match(value.qualification, /discrimination/); assert.equal(value.assertions[0].passed, null);
});
test('reservation uses exact widened float32 division before ceiling, never float64 ceiling', () => {
    const ch = Math.fround(3.6), d = calibration(10, ch);
    assert.equal(Math.ceil(18 / ch), 6); assert.equal(d.placements[0].heightCells, 5);
    const value = inspect(baseline(), { geometry: 'native', sourceId: 'opaque', imageDetails: d });
    assert.equal(value.measurement.reservationCells.rows, 5);
    assert.match(value.qualification, /target/);
    assert.deepEqual(oracleApi.validateStaticImageDetails(d), d);
    d.placements[0].sixelPlacement.cellHeightPx = 3.6;
    assert.throws(() => oracleApi.validateStaticImageDetails(d));
});
test('strict separate image metadata rejects malformed envelopes and safely copies plain data', () => {
    const good = calibration(10, 18), detached = oracleApi.validateStaticImageDetails(good);
    good.placements[0].sixelPlacement.cellWidthPx = 99;
    assert.equal(detached.placements[0].sixelPlacement.cellWidthPx, 10);
    for (const mutate of [d => delete d.schemaVersion, d => d.schemaVersion = 2, d => d.extra = 1,
        d => d.complete = false, d => d.totalPlacements = 2, d => d.placements.push(structuredClone(d.placements[0])),
        d => d.placements[0].extra = 1, d => d.placements[0].bitmapHeightPx = null,
        d => d.placements[0].clipTopRows = -1, d => d.placements[0].placementId = 'unbound',
        d => d.placements[0].sixelPlacement.cellWidthPx = 1.4,
        d => d.placements[0].sixelPlacement.cellWidthPx = NaN,
        d => d.placements[0].sixelPlacement.arithmetic = 'float64-ceiling',
        d => d.placements[0].sixelPlacement.cellWidthOrigin = 'guessed',
        d => Object.defineProperty(d.placements[0], 'col', { get() { throw new Error('getter must not run'); } }),
        d => Object.defineProperty(d, Symbol('hidden'), { value: 1 }),
    ]) {
        const d = calibration(10, 18); mutate(d);
        assert.throws(() => oracleApi.validateStaticImageDetails(d), /Invalid static image comparison/);
    }
});
test('canvas, bitmap, reservation, anchor and clip facts cannot redefine the independent expectation', () => {
    for (const mutate of [p => p.widthCells++, p => p.col++, p => p.clipRightColumns++,
        p => p.bitmapHeightPx = 16, p => p.sixelPlacement.reservationHeightPx = 16, p => p.preserveAspectRatio = true]) {
        const d = calibration(10, 18); mutate(d.placements[0]);
        const value = inspect(projectSixel(1, 1), { geometry: 'native', sourceId: 'opaque', imageDetails: d });
        assert.equal(value.qualification, null); assert.equal(all(value.assertions), false);
    }
});

test('complete zero or multiple draw placements are semantic negatives rather than invented calibration', () => {
    for (const count of [0, 2]) {
        const d = calibration(10, 18); d.totalPlacements = count;
        if (count === 0) d.placements = [];
        else d.placements.push({ ...structuredClone(d.placements[0]), index: 1, placementId: '22222222-2222-3333-4444-555555555555' });
        const value = inspect(projectSixel(1, 1), { geometry: 'native', sourceId: 'opaque', imageDetails: d });
        assert.equal(value.qualification, null); assert.equal(value.assertions[0].passed, false);
    }
});

test('distinct native and whole-cell transforms inside the fixed edge band remain inconclusive', () => {
    const cw = Math.fround(16.25), d = calibration(cw, 18);
    const value = inspect(projectSixel(10 / cw, 1), { geometry: 'native', sourceId: 'opaque', imageDetails: d });
    assert.equal(value.measurement.nativeRaster.passed, true); assert.equal(value.measurement.compatibility.passed, true);
    assert.match(value.qualification, /discrimination/); assert.equal(value.assertions[0].passed, null);
});
