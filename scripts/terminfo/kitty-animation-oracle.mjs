// Pure bounded comparisons and the independent playback timeline; no rendering, I/O or ownership inference.
import { staticImageEnvelope } from './static-images-oracle.mjs';
import { animationBackground, animationFrameIds, animationAppearanceIds, animationAppearanceColor, animationComposedIds, placeholderAppearances, placeholderColors, placeholderGridAppearances, placeholderQuadrantColors, placeholderQuadrantOf } from './kitty-animation-inputs.mjs';
import { animationHoldCycle, animationHoldMs } from './kitty-animation-cases.mjs';

const invalid = () => new Error('Invalid animation comparison.');
const integer = (v, min, max) => Number.isSafeInteger(v) && v >= min && v <= max;
const pixelAt = (image, x, y) => image.pixels.subarray((y * image.width + x) * 4, (y * image.width + x + 1) * 4);
const same = (a, b) => a.every((value, index) => value === b[index]);
const check = (description, passed) => ({ description, passed });
function validateImage(image) {
    if (!image || !integer(image.width, 1, 8192) || !integer(image.height, 1, 8192) || image.width * image.height > 4_000_000
        || !Buffer.isBuffer(image.pixels) || image.pixels.length !== image.width * image.height * 4) throw invalid();
}
const inside = (rect, x, y) => x >= rect.x && y >= rect.y && x < rect.x + rect.width && y < rect.y + rect.height;

function contentBounds(image, envelope) {
    let left = image.width, right = -1, top = image.height, bottom = -1;
    for (let y = envelope.y; y < envelope.y + envelope.height; y++) {
        for (let x = envelope.x; x < envelope.x + envelope.width; x++) {
            if (same(pixelAt(image, x, y), animationBackground)) continue;
            left = Math.min(left, x); right = Math.max(right, x); top = Math.min(top, y); bottom = Math.max(bottom, y);
        }
    }
    return right < left ? null : { x: left, y: top, width: right - left + 1, height: bottom - top + 1 };
}
const regionUniform = (image, rect) => {
    const first = pixelAt(image, rect.x, rect.y);
    for (let y = rect.y; y < rect.y + rect.height; y++) for (let x = rect.x; x < rect.x + rect.width; x++) if (!same(pixelAt(image, x, y), first)) return null;
    return [...first];
};

/** A solid frame color matches exactly; a composed frame's within the rounding step the protocol's blend allows. */
const matchesAppearance = (id, color) => {
    const { color: expected, tolerance } = animationAppearanceColor(id);
    return expected.every((value, index) => Math.abs(value - color[index]) <= tolerance);
};

/**
 * Names the displayed frame from one capture: the non-background content must be the undistorted square the
 * 16x16 source fits into, and its interior (clear of the antialiased edge) must be one exact palette color.
 */
export function classifyAnimationFrame({ image, grid, anchor, cells }) {
    validateImage(image);
    const envelope = staticImageEnvelope(image, grid, anchor, cells), bounds = contentBounds(image, envelope);
    if (!bounds) return { kind: 'background', frameId: null, envelope, bounds: null, geometryOk: true };
    const side = Math.min(envelope.width, envelope.height), geometryOk = bounds.width === side && bounds.height === side;
    const inset = Math.max(1, Math.floor(side / 8)), interior = { x: bounds.x + inset, y: bounds.y + inset, width: bounds.width - 2 * inset, height: bounds.height - 2 * inset };
    const color = interior.width > 0 && interior.height > 0 ? regionUniform(image, interior) : null;
    const frameId = color ? [...animationFrameIds, ...animationComposedIds].find(id => matchesAppearance(id, color)) ?? null : null;
    return { kind: frameId ? 'frame' : 'mixed', frameId, envelope, bounds, geometryOk };
}

/** Independent controls: the baseline target is blank, and nothing outside the target envelope changed. */
export function inspectAnimationPixels({ baseline, observed, grid, anchor, cells, appearance }) {
    validateImage(baseline); validateImage(observed);
    if (baseline.width !== observed.width || baseline.height !== observed.height
        || !(appearance === 'absent' || animationAppearanceIds.includes(appearance))) throw invalid();
    const classification = classifyAnimationFrame({ image: observed, grid, anchor, cells }), envelope = classification.envelope;
    const baselineBlank = classifyAnimationFrame({ image: baseline, grid, anchor, cells }).kind === 'background';
    let outsideUnchanged = true;
    for (let y = 0; y < baseline.height && outsideUnchanged; y++)
        for (let x = 0; x < baseline.width; x++) if (!inside(envelope, x, y) && !same(pixelAt(baseline, x, y), pixelAt(observed, x, y))) { outsideUnchanged = false; break; }
    const controls = [check('reference target is independently blank with the known background', baselineBlank),
        check('all non-target capture pixels remain unchanged', outsideUnchanged)];
    const assertion = appearance === 'absent'
        ? check('no displayed image pixels in target envelope', classification.kind === 'background')
        : check(`undistorted placement filled with the ${appearance} frame color`, classification.kind === 'frame' && classification.geometryOk && classification.frameId === appearance);
    return { scope: 'bounded animation-frame pixel comparison only; no ownership, freshness, timing or stock grade',
        controls, assertions: [assertion], classification: { kind: classification.kind, frameId: classification.frameId, geometryOk: classification.geometryOk },
        measurement: { envelope, contentBounds: classification.bounds } };
}

/**
 * The placeholder suite's reading (#989): the row of cells the stage reads, one color per cell. A cell showing a tile is a band of the image's own color as wide as the cell and as tall as the undistorted
 * fit of the 16x16 source into the grid (its shorter side), with an unpainted strip below or above it; a cell showing nothing is the controlled background throughout. Where the band sits inside the
 * cell is the App's own policy and not asserted, but every painted cell of one row must agree on it.
 */
export function inspectPlaceholderPixels({ baseline, observed, grid, anchor, cells, appearance }) {
    validateImage(baseline); validateImage(observed);
    if (baseline.width !== observed.width || baseline.height !== observed.height || !Object.hasOwn(placeholderAppearances, appearance)) throw invalid();
    const envelope = staticImageEnvelope(observed, grid, anchor, cells), expected = placeholderAppearances[appearance];
    if (expected.length !== cells.cols) throw invalid();
    const cellWidth = grid.cellWidthPx, side = Math.min(cells.cols * cellWidth, cells.rows * grid.cellHeightPx);
    const rects = expected.map((_, index) => ({ x: envelope.x + index * cellWidth, y: envelope.y, width: cellWidth, height: envelope.height }));
    const shown = rects.map(rect => {
        const bounds = contentBounds(observed, rect);
        if (!bounds) return { color: 'absent', bounds: null, geometryOk: true };
        const inset = 1, interior = { x: bounds.x + inset, y: bounds.y + inset, width: bounds.width - 2 * inset, height: bounds.height - 2 * inset };
        const color = interior.width > 0 && interior.height > 0 ? regionUniform(observed, interior) : null;
        const name = color ? Object.keys(placeholderColors).find(id => same(placeholderColors[id], color)) : undefined;
        return { color: name ?? 'mixed', bounds, geometryOk: bounds.width === cellWidth && bounds.height === side };
    });
    const painted = shown.filter(item => item.bounds);
    const aligned = painted.every(item => item.bounds.y === painted[0].bounds.y && item.bounds.height === painted[0].bounds.height);
    const baselineBlank = contentBounds(baseline, envelope) === null;
    let outsideUnchanged = true;
    for (let y = 0; y < baseline.height && outsideUnchanged; y++)
        for (let x = 0; x < baseline.width; x++) if (!inside(envelope, x, y) && !same(pixelAt(baseline, x, y), pixelAt(observed, x, y))) { outsideUnchanged = false; break; }
    const controls = [check('reference target is independently blank with the known background', baselineBlank),
        check('all non-target capture pixels remain unchanged', outsideUnchanged)];
    const matches = shown.every((item, index) => item.color === expected[index] && (item.color === 'absent' || item.geometryOk)) && aligned;
    const assertion = check(`the placeholder cells show [${expected.join(', ')}], each painted tile an undistorted band of its own color`, matches);
    const kind = shown.every(item => item.color === 'absent') ? 'background' : (shown.some(item => item.color === 'mixed') ? 'mixed' : 'tiles');
    const union = painted.length ? { x: Math.min(...painted.map(item => item.bounds.x)), y: Math.min(...painted.map(item => item.bounds.y)),
        width: Math.max(...painted.map(item => item.bounds.x + item.bounds.width)) - Math.min(...painted.map(item => item.bounds.x)), height: painted[0].bounds.height } : null;
    return { scope: 'bounded placeholder-tile pixel comparison only; no ownership, freshness, timing or stock grade',
        controls, assertions: [assertion], classification: { kind, frameId: null, tiles: shown.map(item => item.color), geometryOk: shown.every(item => item.geometryOk) && aligned },
        measurement: { envelope, contentBounds: union, cells: shown.map((item, index) => ({ rect: rects[index], color: item.color, bounds: item.bounds })) } };
}

/**
 * The placeholder grid suite's reading (#989): a grid of cells, one letter per cell (see placeholderGridAppearances). The 16x16 source, in four colored quadrants, fits the grid undistorted as a square whose side is the grid's shorter
 * side, and each quadrant is a square of half that side. A cell shows the tile of the grid the marks name: the tile's row is the image's quadrant row (a cell is as tall as half the square, give or take the slack, so tile row 0 lies in the
 * upper quadrants and tile row 1 in the lower ones) and its column is the cell's own (the grid suite never swaps columns). Where the square sits in the grid is the App's own policy and is not asserted, so a painted cell is sampled only
 * where its quadrant is under every placement of the square (the grid's slack along each axis), one pixel clear of the quadrant's edge, translated into the cell from the tile's own place: that sample must be one exact color.
 * A cell that shows nothing must be the controlled background throughout.
 */
export function inspectPlaceholderGridPixels({ baseline, observed, grid, anchor, cells, appearance }) {
    validateImage(baseline); validateImage(observed);
    if (baseline.width !== observed.width || baseline.height !== observed.height || !Object.hasOwn(placeholderGridAppearances, appearance)) throw invalid();
    const envelope = staticImageEnvelope(observed, grid, anchor, cells), expected = placeholderGridAppearances[appearance].split('/');
    if (expected.length !== cells.rows || expected.some(row => row.length !== cells.cols)) throw invalid();
    const cw = grid.cellWidthPx, ch = grid.cellHeightPx, side = Math.min(cells.cols * cw, cells.rows * ch), half = Math.floor(side / 2);
    const slackX = cells.cols * cw - side, slackY = cells.rows * ch - side;
    if (ch < half || ch > half + slackY + 1) throw invalid(); // a cell this far from half the square would put a tile row across two quadrants
    // The part of one quadrant (grid-relative pixels) that every placement of the square keeps, a pixel clear of the quadrant's edges.
    const safe = ([row, column]) => ({ x0: slackX + column * half + 1, x1: (column + 1) * half - 1, y0: slackY + row * half + 1, y1: (row + 1) * half - 1 });
    const shown = expected.map((line, row) => [...line].map((letter, column) => {
        const rect = { x: envelope.x + column * cw, y: envelope.y + row * ch, width: cw, height: ch };
        if (letter === '.') return { expected: 'absent', color: contentBounds(observed, rect) === null ? 'absent' : 'mixed', rect, sample: null };
        const quadrant = placeholderQuadrantOf[letter], q = safe(quadrant), tileRow = quadrant[0];
        // The safe part of the quadrant, in grid pixels, is where the tile sits in the grid (its row from the quadrant, its column the cell's own); the cell shows the tile at the cell's own place.
        const sample = { x: rect.x + Math.max(0, q.x0 - column * cw), y: rect.y + Math.max(0, q.y0 - tileRow * ch) };
        sample.width = rect.x + Math.min(cw, q.x1 - column * cw) - sample.x; sample.height = rect.y + Math.min(ch, q.y1 - tileRow * ch) - sample.y;
        if (sample.width <= 0 || sample.height <= 0) throw invalid();
        const color = regionUniform(observed, sample);
        const name = color ? Object.keys(placeholderQuadrantColors).find(id => same(placeholderQuadrantColors[id], color)) : undefined;
        return { expected: letter, color: name ?? 'mixed', rect, sample };
    }));
    const flat = shown.flat();
    const baselineBlank = contentBounds(baseline, envelope) === null;
    let outsideUnchanged = true;
    for (let y = 0; y < baseline.height && outsideUnchanged; y++)
        for (let x = 0; x < baseline.width; x++) if (!inside(envelope, x, y) && !same(pixelAt(baseline, x, y), pixelAt(observed, x, y))) { outsideUnchanged = false; break; }
    const controls = [check('reference target is independently blank with the known background', baselineBlank),
        check('all non-target capture pixels remain unchanged', outsideUnchanged)];
    const assertion = check(`the grid of placeholder cells shows ${expected.join('/')}, each painted cell the exact color of its quadrant`, flat.every(item => item.color === item.expected));
    const kind = flat.every(item => item.expected === 'absent' && item.color === 'absent') ? 'background' : (flat.some(item => item.color === 'mixed') ? 'mixed' : 'tiles');
    return { scope: 'bounded placeholder-grid pixel comparison only; no ownership, freshness, timing or stock grade',
        controls, assertions: [assertion], classification: { kind, frameId: null, cells: shown.map(line => line.map(item => (item.color === 'absent' || item.color === 'mixed' ? (item.color === 'absent' ? '.' : '?') : item.color)).join('')).join('/'), geometryOk: true },
        measurement: { envelope, contentBounds: null, cells: flat.map(item => ({ rect: item.rect, sample: item.sample, expected: item.expected, color: item.color })) } };
}

const cycleMs = animationHoldCycle.length * animationHoldMs;
/**
 * Frames a looping animation could be showing for every start time in [start.earliest, start.latest] and every
 * instant in [window.start, window.end]. A window before the animation could have started shows the root.
 */
export function framesPossibleAt(start, window) {
    for (const value of [start?.earliest, start?.latest, window?.start, window?.end]) if (!Number.isFinite(value)) throw invalid();
    if (start.earliest > start.latest || window.start > window.end) throw invalid();
    const low = window.start - start.latest, high = window.end - start.earliest;
    const set = new Set();
    if (low < 0) set.add(animationHoldCycle[0]);
    if (high < 0) return [...set];
    if (high - Math.max(low, 0) >= cycleMs) return [...animationHoldCycle];
    for (let slot = Math.floor(Math.max(low, 0) / animationHoldMs); slot <= Math.floor(high / animationHoldMs); slot++) set.add(animationHoldCycle[slot % animationHoldCycle.length]);
    return animationHoldCycle.filter(id => set.has(id));
}

const timingOk = Object.freeze({ valid: true, reason: null }), timingNo = reason => ({ valid: false, reason });
function runningTiming(stage, facts) {
    if (!facts.start) return timingNo('animation start time unavailable');
    const possible = framesPossibleAt(facts.start, facts.capture);
    if (possible.length !== 1) return timingNo('capture span straddles a frame boundary: ' + possible.join('/'));
    // A timeline stage shows whichever frame the original start gives for the capture span (the one frame `possible` names).
    const expected = stage.appearance === 'timeline' ? possible[0] : stage.appearance;
    // After a relaunch a restored playhead and one the new App restarted must predict different frames here, or this read cannot tell a restore from a restart.
    if (facts.relaunch && framesPossibleAt(facts.relaunch, facts.capture).includes(expected)) {
        return timingNo(`a playhead restarted by the relaunch could show the ${expected} frame here as well`);
    }
    if (stage.appearance === 'timeline') return timingOk;
    return possible[0] === stage.appearance ? timingOk : timingNo(`read landed in the ${possible[0]} window, not the planned ${stage.appearance} window`);
}
/** The one frame the timeline allows for a stage whose frame depends on when it was read; undefined when the capture span could show more than one (the stage is then inconclusive). */
export function timelineAppearance(facts) {
    if (!facts.start || !facts.capture) return undefined;
    const possible = framesPossibleAt(facts.start, facts.capture);
    return possible.length === 1 ? possible[0] : undefined;
}
function frozenTiming(stage, facts) {
    const { capture } = facts;
    if (!facts.start) return facts.build && capture.start - facts.build.latest >= animationHoldMs
        ? timingOk : timingNo('read did not outlast the root hold since the frame was built');
    if (!facts.stop) return timingNo('stop time unavailable');
    const frozenAt = framesPossibleAt(facts.start, { start: facts.stop.earliest, end: facts.stop.latest });
    if (frozenAt.length !== 1 || frozenAt[0] !== stage.appearance) return timingNo('stop did not land inside the ' + stage.appearance + ' window');
    if (capture.start < facts.stop.latest) return timingNo('read precedes the stop taking effect');
    // The first read after a relaunch must also rule out a playhead the App restarted from the root: it shows the root early on, so only a capture soon enough after the relaunch can tell it from a stop.
    if (stage.relaunch && facts.relaunch && framesPossibleAt(facts.relaunch, capture).includes(stage.appearance)) {
        return timingNo(`a playhead restarted from the root by the relaunch could show the ${stage.appearance} frame here as well`);
    }
    if (stage.waitMs === undefined) return timingOk;
    if (framesPossibleAt(facts.start, capture).includes(stage.appearance)) return timingNo('a running animation could still show ' + stage.appearance + ' here');
    return resumedPlayhead(stage, facts) ? timingNo('a playhead resumed from the frozen frame by the relaunch could still show ' + stage.appearance + ' here') : timingOk;
}
/**
 * After a relaunch a stopped animation must also be told apart from one that resumed from its frozen frame when the App came back: such a playhead shows the frame it resumed on for
 * at most one hold from the moment it resumed (anywhere in the relaunch window), so its start time ranges from two holds before the window (it resumed late in the frame's own hold)
 * to one hold before the window's end, shifted by the frame's place in the cycle.
 */
function resumedPlayhead(stage, facts) {
    if (!facts.relaunch) return false;
    const position = animationHoldCycle.indexOf(stage.appearance) * animationHoldMs;
    const resumed = { earliest: facts.relaunch.earliest - position - animationHoldMs, latest: facts.relaunch.latest - position };
    return framesPossibleAt(resumed, facts.capture).includes(stage.appearance);
}
/**
 * Whether the timing facts let a read of this stage stand for its declared frame. Invalid timing makes a stage
 * inconclusive (a read outside its window says nothing about playback); it never becomes a failure.
 * facts: { start, stop, build } are { earliest, latest } milliseconds a command took effect within; capture is
 * { start, end }, the wall-clock span of the paired image captures.
 */
export function evaluateAnimationTiming(stage, facts) {
    if (stage.clock === 'static') return timingOk;
    const capture = facts.capture;
    if (!capture || !Number.isFinite(capture.start) || !Number.isFinite(capture.end) || capture.start > capture.end) return timingNo('capture span unavailable');
    return stage.clock === 'running' ? runningTiming(stage, facts) : frozenTiming(stage, facts);
}
