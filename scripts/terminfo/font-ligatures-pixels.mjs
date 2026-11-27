import { isDeepStrictEqual as same } from 'node:util';
import { family, fixtures, fontSha256, fontSize, requireLigatures as check, selections } from './font-ligatures-contracts.mjs';

const rgb = argb => [argb >>> 16 & 255, argb >>> 8 & 255, argb & 255];

function inkCoverage(image, box, theme) {
    const bg = rgb(theme.backgroundArgb), fg = rgb(theme.foregroundArgb);
    const deltas = fg.map((component, i) => component - bg[i]);
    const contrast = deltas.reduce((sum, delta) => sum + delta ** 2, 0);
    check(contrast > 10000, 'insufficient glyph contrast');
    const values = [];
    for (let y = box.y; y < box.y + box.height; y++) {
        for (let x = box.x; x < box.x + box.width; x++) {
            const offset = (y * image.width + x) * 4;
            check(image.pixels[offset + 3] === 255, 'unexpected crop transparency');
            const projection = deltas.reduce((sum, delta, i) => sum + (image.pixels[offset + i] - bg[i]) * delta, 0);
            values.push(Math.min(1, Math.max(0, projection / contrast)));
        }
    }
    return values;
}

const distance = (a, b) => a.reduce((sum, value, i) => sum + Math.abs(value - b[i]), 0);
const round = value => Math.round(100 * value) / 100;

function agreeWithOutline(actual, expected, other) {
    const match = distance(actual, expected), mismatch = distance(actual, other);
    const ink = expected.reduce((sum, value) => sum + value, 0);
    check(ink > 0 && match / ink < 0.45 && mismatch - match > 0.30 * distance(expected, other),
        'crop disagrees with the expected glyph outlines');
    return { expectedError: round(match), alternativeError: round(mismatch) };
}

function contains(rect, x, y) {
    return x >= rect.x && x < rect.x + rect.width && y >= rect.y && y < rect.y + rect.height;
}

function changes(a, b, boxes) {
    check(a.width === b.width && a.height === b.height, 'pixel dimensions changed');
    const cells = boxes.map(() => 0);
    let outside = 0;
    for (let y = 0; y < a.height; y++) {
        for (let x = 0; x < a.width; x++) {
            const offset = (y * a.width + x) * 4;
            if (a.pixels.readUInt32BE(offset) === b.pixels.readUInt32BE(offset)) continue;
            const target = boxes.findIndex(box => contains(box, x, y));
            if (target < 0) outside++;
            else cells[target]++;
        }
    }
    return { cells, outside };
}

function masks(oracle, fixture) {
    const expected = oracle.fixtures[fixture.pair];
    return ['ordinary', 'shaped'].map(kind => {
        check(same(expected[kind].map(g => g.id), fixture[kind])
            && expected[kind].every((g, i) => g.cluster === i && g.advance === 1200 && g.xOffset === 0 && g.yOffset === 0),
        'independent shaping result disagrees with the pinned font expectation');
        const rows = expected[kind + 'Mask'];
        check(rows.length === oracle.box.height && rows.every(line => line.length === oracle.box.width && /^[0-4]+$/.test(line)),
            'invalid glyph outline coverage');
        return Array.from(rows.join(''), coverage => Number(coverage) / 4);
    });
}

function consistentContext(view) {
    const { enableLigatures, syntheticSelection, ...rest } = view.details;
    return rest;
}

export function gradePixels(runs, oracle) {
    const [baseline, off, on, restored] = runs.map(run => run.views[0].image);
    const details = runs[1].views[0].details, { grid, theme } = details;
    check(runs.every(run => run.views.every(view => same(consistentContext(view), consistentContext(runs[1].views[0])))),
        'font, grid, theme or presentation changed between fixture renders');
    check(oracle.fontSha256 === fontSha256 && oracle.family === family && oracle.version === 'Version 6.002'
        && oracle.postscript === 'FiraCode-Regular' && oracle.sizeDip === fontSize
        && same(oracle.box, { width: 30, height: 32, yFromBaseline: -24 }) && grid.cellWidthPx * 2 === oracle.box.width,
    'oracle or cell geometry differs from the fixed font preflight');
    check(off.pixels.equals(restored.pixels), 'off pixels did not restore');
    const targets = fixtures.map(({ row, start }) => ({
        x: grid.originXPx + start * grid.cellWidthPx, y: grid.originYPx + row * grid.cellHeightPx,
        width: 2 * grid.cellWidthPx, height: grid.cellHeightPx,
    }));
    const toggle = changes(off, on, targets);
    check(toggle.outside === 0 && toggle.cells.every(count => count > 0), 'toggle did not confine changes to both operators');
    check(baseline.pixels.equals(off.pixels), 'baseline on did not reproduce unshaped rendering');
    const observations = fixtures.map((fixture, i) => {
        const target = targets[i];
        const crop = { x: target.x, y: target.y + grid.baselineOffsetPx + oracle.box.yFromBaseline,
            width: oracle.box.width, height: oracle.box.height };
        const [plainMask, shapedMask] = masks(oracle, fixture);
        const ordinary = agreeWithOutline(inkCoverage(off, crop, theme), plainMask, shapedMask);
        const shaped = agreeWithOutline(inkCoverage(on, crop, theme), shapedMask, plainMask);
        const selectedOff = runs[1].views[1 + i].image, selectedOn = runs[2].views[1 + i].image;
        const selectedChanges = changes(on, selectedOn, [target]);
        check(selectedChanges.outside === 0 && selectedChanges.cells[0] > 0, 'selection did not confine changes to the operator');
        check(changes(selectedOff, selectedOn, [target]).cells[0] === 0, 'partial selection did not reproduce ordinary glyphs');
        const selectedCell = { ...target, x: target.x + (selections[1 + i].startCol - fixture.start) * grid.cellWidthPx,
            width: grid.cellWidthPx };
        const selectedOrdinaryChanges = changes(off, selectedOff, [selectedCell]);
        check(selectedOrdinaryChanges.outside === 0 && selectedOrdinaryChanges.cells[0] > 0, 'ordinary selection control did not cover exactly one cell');
        return { pair: fixture.pair, text: fixture.text, crop, changedPixels: toggle.cells[i], ordinary, shaped,
            selection: selections[1 + i], selectedChangedPixels: selectedChanges.cells[0], selectionMatchesOrdinary: true };
    });
    return { outcome: 'supported', baseline: 'unsupported', restored: true, changedPixelsOutsideOperators: toggle.outside, observations };
}
