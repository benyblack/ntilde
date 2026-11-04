// Pure bounded pixel comparisons; no rendering, image decoding, I/O or terminal ownership inference.
import { expectedStaticSourcePixel, staticImageBackground, staticSixelCanvas } from './static-images-inputs.mjs';

const integer = (v, min, max) => Number.isSafeInteger(v) && v >= min && v <= max;
const invalid = () => new Error('Invalid static image comparison.');
const pixelAt = (image, x, y) => image.pixels.subarray((y * image.width + x) * 4, (y * image.width + x + 1) * 4);
const same = (a, b) => a.every((value, index) => value === b[index]);
const check = (description, passed) => ({ description, passed });
const defaultCells = Object.freeze({ cols: 4, rows: 2 });
function validateImage(image) {
    if (!image || !integer(image.width, 1, 8192) || !integer(image.height, 1, 8192)
        || image.width * image.height > 4_000_000 || !Buffer.isBuffer(image.pixels)
        || image.pixels.length !== image.width * image.height * 4) throw invalid();
}

/** Cell envelope derives only from the actual controlled capture grid, not a guessed font/DPI. */
export function staticImageEnvelope(image, grid, anchor, cells = defaultCells) {
    validateImage(image);
    if (!grid || !anchor || !cells || !integer(grid.originXPx, 0, image.width) || !integer(grid.originYPx, 0, image.height)
        || !integer(grid.cellWidthPx, 1, 256) || !integer(grid.cellHeightPx, 1, 512)
        || grid.renderWidthPx !== image.width || grid.renderHeightPx !== image.height
        || !integer(anchor.col, 0, 1000) || !integer(anchor.row, 0, 1000)
        || !integer(cells.cols, 1, 8) || !integer(cells.rows, 1, 8)) throw invalid();
    const bounds = { x: grid.originXPx + anchor.col * grid.cellWidthPx, y: grid.originYPx + anchor.row * grid.cellHeightPx,
        width: cells.cols * grid.cellWidthPx, height: cells.rows * grid.cellHeightPx };
    if (bounds.x + bounds.width > image.width || bounds.y + bounds.height > image.height) throw invalid();
    return bounds;
}
const inside = (rect, x, y) => x >= rect.x && y >= rect.y && x < rect.x + rect.width && y < rect.y + rect.height;
function contentBounds(image, envelope) {
    let left = image.width, right = -1, top = image.height, bottom = -1;
    for (let y = envelope.y; y < envelope.y + envelope.height; y++) {
        for (let x = envelope.x; x < envelope.x + envelope.width; x++) {
            if (same(pixelAt(image, x, y), staticImageBackground)) continue;
            left = Math.min(left, x); right = Math.max(right, x); top = Math.min(top, y); bottom = Math.max(bottom, y);
        }
    }
    return right < left ? null : { x: left, y: top, width: right - left + 1, height: bottom - top + 1 };
}
function outsideUnchanged(before, observed, envelope) {
    for (let y = 0; y < before.height; y++) {
        for (let x = 0; x < before.width; x++) {
            if (!inside(envelope, x, y) && !same(pixelAt(before, x, y), pixelAt(observed, x, y))) return false;
        }
    }
    return true;
}
function backgroundOnly(image, envelope) {
    for (let y = envelope.y; y < envelope.y + envelope.height; y++) {
        for (let x = envelope.x; x < envelope.x + envelope.width; x++) {
            if (!same(pixelAt(image, x, y), staticImageBackground)) return false;
        }
    }
    return true;
}
function correctGeometry(bounds, envelope, geometry) {
    if (!bounds) return false;
    if (geometry === 'stretch') return Object.keys(envelope).every(key => bounds[key] === envelope[key]);
    // The primary protocols require undistorted fit, but do not define a shared padding alignment.
    // Qualify the contained square's size/aspect, not a guessed centering convention.
    const side = Math.min(envelope.width, envelope.height);
    return bounds.width === side && bounds.height === side;
}
function interiorSamples(image, bounds, sourceId) {
    if (!bounds) return [];
    // Source-space interior positions are fixed independently of the renderer's interpolation.
    const points = [[2.5, 3.5], [10.5, 3.5], [1.5, 13.5], [10.5, 13.5]];
    if (sourceId !== 'opaque') points.push([3, 10]);
    return points.map(([u, v], index) => {
        const x = bounds.x + Math.min(bounds.width - 1, Math.floor(u * bounds.width / 16));
        const y = bounds.y + Math.min(bounds.height - 1, Math.floor(v * bounds.height / 16));
        const source = expectedStaticSourcePixel(sourceId, Math.floor(u), Math.floor(v));
        const expected = source[3] === 0 ? staticImageBackground : source;
        return check(index === 4 ? 'alpha-zero interior reveals the known background' : `asymmetric color interior ${index}`,
            same(pixelAt(image, x, y), expected));
    });
}

/** Returns independent controls/assertions, never a support grade or proof of a real runtime. */
export function inspectStaticImagePixels({ baseline, observed, grid, anchor, cells, geometry, sourceId = 'alpha', visible = true, imageDetails }) {
    validateImage(baseline); validateImage(observed);
    expectedStaticSourcePixel(sourceId, 0, 0);
    if ((geometry === 'native' && sourceId !== 'opaque') || baseline.width !== observed.width || baseline.height !== observed.height || !['native', 'stretch', 'aspect'].includes(geometry)
        || typeof visible !== 'boolean') throw invalid();
    const envelope = staticImageEnvelope(observed, grid, anchor, cells);
    const bounds = contentBounds(observed, envelope);
    const controls = [check('reference target is independently blank with the known background', backgroundOnly(baseline, envelope)),
        check('all non-target capture pixels remain unchanged', outsideUnchanged(baseline, observed, envelope))];
    const native = geometry === 'native' && visible ? inspectNative(observed, envelope, grid, validateStaticImageDetails(imageDetails)) : null;
    const assertions = native ? native.assertions : visible
        ? [check('source-backed native/stretch/aspect geometry', correctGeometry(bounds, envelope, geometry)), ...interiorSamples(observed, bounds, sourceId)]
        : [check('no displayed image pixels in target envelope', bounds === null)];
    return { scope: 'bounded static-image pixel/geometry comparison only; no ownership, live execution, freshness or stock grade',
        controls, assertions, qualification: native?.qualification ?? null,
        measurement: { envelope, contentBounds: bounds, sourceWidth: 16, sourceHeight: 16, geometry, ...(native?.measurement ?? {}) } };
}

const placementKeys = ['index', 'placementId', 'col', 'viewportRow', 'widthCells', 'heightCells', 'clipTopRows', 'clipBottomRows',
    'clipRightColumns', 'preserveAspectRatio', 'bitmapWidthPx', 'bitmapHeightPx', 'sixelPlacement'];
const provenanceKeys = ['reservationWidthPx', 'reservationHeightPx', 'dimensionOrigin', 'cellWidthPx', 'cellHeightPx',
    'cellWidthOrigin', 'cellHeightOrigin', 'arithmetic'];
const origins = ['sixel-override', 'parser-metric', 'default-fallback'];
function requireFact(value) { if (!value) throw invalid(); }
function dataObject(value, keys) {
    requireFact(value && [Object.prototype, null].includes(Object.getPrototypeOf(value)));
    const descriptors = Object.getOwnPropertyDescriptors(value), actual = Reflect.ownKeys(descriptors);
    requireFact(actual.length === keys.length && keys.every(key => Object.hasOwn(descriptors, key)
        && Object.hasOwn(descriptors[key], 'value')));
    return Object.fromEntries(keys.map(key => [key, descriptors[key].value]));
}
function dataArray(value) {
    requireFact(Array.isArray(value) && Object.getPrototypeOf(value) === Array.prototype && value.length <= 512);
    const descriptors = Object.getOwnPropertyDescriptors(value);
    requireFact(Reflect.ownKeys(descriptors).length === value.length + 1);
    return Array.from({ length: value.length }, (_, index) => {
        requireFact(Object.hasOwn(descriptors[index] ?? {}, 'value')); return descriptors[index].value;
    });
}
function copyProvenance(value) {
    if (value === null) return null;
    const p = dataObject(value, provenanceKeys);
    requireFact(integer(p.reservationWidthPx, 1, 8192) && integer(p.reservationHeightPx, 1, 8192)
        && ['sixel-geometry', 'image-decoder'].includes(p.dimensionOrigin) && p.arithmetic === 'float32-ceiling-v1');
    for (const axis of ['Width', 'Height']) {
        const v = p['cell' + axis + 'Px'];
        requireFact(Number.isFinite(v) && v >= 1 && Math.fround(v) === v && origins.includes(p['cell' + axis + 'Origin']));
    }
    return p;
}
function copyPlacement(value, index) {
    const p = dataObject(value, placementKeys);
    requireFact(p.index === index && typeof p.placementId === 'string' && /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/.test(p.placementId)
        && typeof p.preserveAspectRatio === 'boolean');
    for (const key of ['col', 'viewportRow']) requireFact(integer(p[key], -0x80000000, 0x7fffffff));
    for (const key of ['widthCells', 'heightCells']) requireFact(integer(p[key], 1, 0x7fffffff));
    for (const key of ['clipTopRows', 'clipBottomRows', 'clipRightColumns']) requireFact(integer(p[key], 0, 0x7fffffff));
    requireFact(p.clipTopRows + p.clipBottomRows <= p.heightCells && p.clipRightColumns <= p.widthCells);
    requireFact((p.bitmapWidthPx === null && p.bitmapHeightPx === null)
        || (integer(p.bitmapWidthPx, 1, 8192) && integer(p.bitmapHeightPx, 1, 8192)));
    return { ...p, sixelPlacement: copyProvenance(p.sixelPlacement) };
}
/** Strict detached exact-draw envelope. Valid unknown creation provenance stays explicitly null. */
export function validateStaticImageDetails(value) {
    const d = dataObject(value, ['schemaVersion', 'complete', 'totalPlacements', 'placements']);
    const array = dataArray(d.placements);
    requireFact(d.schemaVersion === 1 && d.complete === true && integer(d.totalPlacements, 0, 512) && array.length === d.totalPlacements);
    const placements = array.map(copyPlacement);
    requireFact(new Set(placements.map(p => p.placementId)).size === placements.length);
    return { ...d, placements };
}
function unqualifiedNative(reason, measurement = {}) {
    return { qualification: reason, assertions: [check('calibrated native raster fidelity', null)], measurement };
}
function nativeRegions(projection) {
    const { x, y, sx, sy } = projection;
    return [
        { x, y, width: 5 * sx, height: 7 * sy, color: [255, 0, 0, 255] },
        { x: x + 5 * sx, y, width: 11 * sx, height: 7 * sy, color: [0, 255, 0, 255] },
        { x, y: y + 7 * sy, width: 5 * sx, height: 9 * sy, color: [0, 0, 255, 255] },
        { x: x + 5 * sx, y: y + 7 * sy, width: 11 * sx, height: 9 * sy, color: [255, 255, 0, 255] },
    ];
}
function inEdgeBand(rect, x, y) {
    const dx = Math.min(Math.abs(x - rect.x), Math.abs(x - rect.x - rect.width));
    const dy = Math.min(Math.abs(y - rect.y), Math.abs(y - rect.y - rect.height));
    return (dx <= 1 && y >= rect.y - 1 && y <= rect.y + rect.height + 1)
        || (dy <= 1 && x >= rect.x - 1 && x <= rect.x + rect.width + 1);
}
function analyticRaster(image, envelope, projection) {
    const regions = nativeRegions(projection), interiors = [0, 0, 0, 0];
    let outside = 0, uncertain = 0, mismatches = 0;
    for (let y = envelope.y; y < envelope.y + envelope.height; y++) for (let x = envelope.x; x < envelope.x + envelope.width; x++) {
        const cx = x + 0.5, cy = y + 0.5;
        if (regions.some(rect => inEdgeBand(rect, cx, cy))) { uncertain++; continue; }
        const index = regions.findIndex(rect => inside(rect, cx, cy));
        if (index < 0) outside++; else interiors[index]++;
        if (!same(pixelAt(image, x, y), index < 0 ? staticImageBackground : regions[index].color)) mismatches++;
    }
    const discriminating = interiors.every(n => n > 0) && outside > 0;
    return { passed: discriminating ? mismatches === 0 : null, mismatches, safeColorPixels: interiors, safeBackgroundPixels: outside,
        uncertaintyPixels: uncertain, edgeTolerancePx: 1, discriminating };
}
function nativePlacementAssertions(p, source, grid, envelope) {
    const provenance = p.sixelPlacement;
    const nx = Math.ceil(Math.fround(source.width / provenance.cellWidthPx));
    const ny = Math.ceil(Math.fround(source.height / provenance.cellHeightPx));
    return [check('independent source canvas matches reservation and actual bitmap dimensions',
        provenance.reservationWidthPx === source.width && provenance.reservationHeightPx === source.height
        && p.bitmapWidthPx === source.width && p.bitmapHeightPx === source.height),
    check('independent float32 ceiling reservation, anchor and unclipped native intent', p.widthCells === nx && p.heightCells === ny
        && grid.originXPx + p.col * grid.cellWidthPx === envelope.x && grid.originYPx + p.viewportRow * grid.cellHeightPx === envelope.y
        && p.clipTopRows === 0 && p.clipBottomRows === 0 && p.clipRightColumns === 0 && !p.preserveAspectRatio)];
}
function inspectNative(image, envelope, grid, details) {
    if (details.placements.length !== 1) return { qualification: null,
        assertions: [check('exactly one SIXEL draw placement', false)], measurement: { placementCount: details.placements.length } };
    const p = details.placements[0], c = p.sixelPlacement;
    if (!c) return unqualifiedNative('missing SIXEL creation provenance');
    if (c.cellWidthOrigin !== 'sixel-override' || c.cellHeightOrigin !== 'sixel-override')
        return unqualifiedNative('physical direct-local SIXEL override provenance is required');
    const source = staticSixelCanvas(), sx = grid.cellWidthPx / c.cellWidthPx, sy = grid.cellHeightPx / c.cellHeightPx;
    const projection = { x: envelope.x, y: envelope.y, sx, sy };
    const nativeProjection = { x: envelope.x, y: envelope.y, width: source.width * sx, height: source.height * sy,
        paintedWidth: source.paintedWidth * sx, paintedHeight: source.paintedHeight * sy };
    const nx = Math.ceil(Math.fround(source.width / c.cellWidthPx)), ny = Math.ceil(Math.fround(source.height / c.cellHeightPx));
    const compatibilityProjection = { ...projection, sx: nx * grid.cellWidthPx / source.width, sy: ny * grid.cellHeightPx / source.height };
    const measurement = { sourceCanvas: source, nativeProjection, reservationCells: { cols: nx, rows: ny },
        compatibilityProjection: { width: nx * grid.cellWidthPx, height: ny * grid.cellHeightPx } };
    const placement = nativePlacementAssertions(p, source, grid, envelope);
    if (!placement.every(a => a.passed)) return { qualification: null, assertions: placement, measurement };
    if (nativeProjection.width + 1 > envelope.width || nativeProjection.height + 1 > envelope.height
        || nx * grid.cellWidthPx + 1 > envelope.width || ny * grid.cellHeightPx + 1 > envelope.height)
        return unqualifiedNative('insufficient independently blank target for calibrated native projection', measurement);
    const native = analyticRaster(image, envelope, projection), compatibility = analyticRaster(image, envelope, compatibilityProjection);
    Object.assign(measurement, { nativeRaster: native, compatibility });
    if (!native.discriminating || (native.passed && compatibility.passed && (sx !== compatibilityProjection.sx || sy !== compatibilityProjection.sy)))
        return unqualifiedNative('insufficient independent raster discrimination outside the fixed edge band', measurement);
    return { qualification: null, assertions: [check('calibrated native raster fidelity', native.passed), ...placement], measurement };
}
