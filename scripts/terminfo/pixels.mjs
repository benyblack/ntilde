import { screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { pristineCursorBaseline } from './cursor.mjs';
import { pixelPlan, pixelScenes, pixelSuiteId, pixelRegion, blankRegion, controlRegion, cursorRegion } from './pixels-cases.mjs';
import { retainedPixel, stablePixelContext, totalPixelLimit, defaultCursorPolicy } from './pixels-capture.mjs';

/** Exact cell rectangles from the actual pixel grid, bounded inside this PNG. */
export function regionPixels(image, grid, region) {
    const x = grid.originXPx + region.col * grid.cellWidthPx, y = grid.originYPx + region.row * grid.cellHeightPx;
    const width = region.cols * grid.cellWidthPx, height = region.rows * grid.cellHeightPx;
    if (![x, y, width, height].every(Number.isSafeInteger) || x < 0 || y < 0 || width < 1 || height < 1
        || x + width > image.width || y + height > image.height) throw new Error('Pixel region is outside the capture.');
    const bytes = Buffer.alloc(width * height * 4);
    for (let row = 0; row < height; row++) image.pixels.copy(bytes, row * width * 4,
        ((y + row) * image.width + x) * 4, ((y + row) * image.width + x + width) * 4);
    return bytes;
}
const colorBytes = argb => [(argb >>> 16) & 255, (argb >>> 8) & 255, argb & 255, argb >>> 24];
function countColor(bytes, rgba) {
    let count = 0;
    for (let offset = 0; offset < bytes.length; offset += 4) {
        if (rgba.every((value, channel) => bytes[offset + channel] === value)) count++;
    }
    return count;
}
// Skia strokes may cover fractions of a pixel; require the requested color's
// blend with the known opaque background, rather than exact opaque stroke pixels.
export function countColorCoverage(bytes, foreground, background = [16, 16, 16, 255]) {
    const channel = [0, 1, 2].sort((a, b) => Math.abs(foreground[b] - background[b]) - Math.abs(foreground[a] - background[a]))[0];
    const delta = foreground[channel] - background[channel];
    if (!delta) return 0;
    let count = 0;
    for (let offset = 0; offset < bytes.length; offset += 4) {
        const coverage = (bytes[offset + channel] - background[channel]) / delta;
        if (coverage >= 0.1 && coverage <= 1.01 && bytes[offset + 3] === 255
            && [0, 1, 2].every(c => Math.abs(bytes[offset + c] - (background[c] + coverage * (foreground[c] - background[c]))) <= 2)) count++;
    }
    return count;
}
const solid = (bytes, rgba) => countColor(bytes, rgba) === bytes.length / 4;
const changed = (left, right) => !left.equals(right);
function sceneContentReady(frame) {
    const screen = frame.screen, lines = Array(screen.rows).fill('');
    if (frame.sceneId === 'colors') {
        lines[4] = '  ' + '\u2588'.repeat(32); lines[6] = '      ' + '\u2588'.repeat(4);
    } else { lines[2] = '    HMWqg_|'; lines[6] = lines[2]; }
    return screen.cursorRow === 8 && screen.cursorCol === 8 && snapshotHash(screen.lines) === snapshotHash(lines);
}
function expectedCursorMode(parameter) {
    if (parameter === 'default') return { style: 2, blink: true, isDefault: true };
    const code = parameter === 'hidden' ? 1 : Number(parameter);
    let style = 0;
    if (code >= 5) style = 1;
    else if (code >= 3) style = 2;
    return { style, blink: code === 0 || code % 2 === 1, isDefault: false };
}
const ink = bytes => bytes.length / 4 - countColor(bytes, [16, 16, 16, 255]);

/** Revalidate the complete journal and every retained PNG before calculating any observations. */
export function validatePixelFrames(frames, runId, output) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '') || !Array.isArray(frames) || frames.length !== pixelPlan.length + 3) throw new Error('Incomplete pixel journal.');
    if (frames.some(frame => !Number.isSafeInteger(frame.pixel?.width) || !Number.isSafeInteger(frame.pixel?.height)
        || frame.pixel.width < 1 || frame.pixel.height < 1)
        || frames.reduce((sum, frame) => sum + frame.pixel.width * frame.pixel.height, 0) > totalPixelLimit) throw new Error('Pixel suite decoded budget exhausted.');
    const first = frames[0], last = frames.at(-1);
    const pane = first.paneId, context = stablePixelContext(first.pixel.renderDetails);
    const defaultPolicy = defaultCursorPolicy(first.screen, first.pixel.renderDetails);
    if (!pristineCursorBaseline(first.screen) || first.screen.cellDetails.some(cell => cell.overline) || snapshotHash(first.screen) !== snapshotHash(last.screen)
        || first.pixel.sha256 !== last.pixel.sha256) throw new Error('Pixel restoration was not verified.');
    const images = frames.map((frame, index) => {
        const plan = index >= 2 && index < frames.length - 1 ? pixelPlan[index - 2] : { timeMs: 0 };
        const kind = index === 0 ? 'pixel-start' : index === 1 ? 'pixel-bind' : index === frames.length - 1 ? 'pixel-finish' : 'pixel-read';
        if (frame.index !== index || frame.kind !== kind || frame.paneId !== pane || !/^[a-f\d-]{36}$/i.test(pane ?? '')
            || frame.sha256 !== snapshotHash(screenSnapshot(frame.screen, false, true, true, true))
            || frame.afterSha256 !== frame.sha256 || snapshotHash(screenSnapshot(frame.afterScreen, false, true, true, true)) !== frame.sha256 || frame.screen.rows !== first.screen.rows || frame.screen.cols !== first.screen.cols
            || frame.pixel.presentationTimeMs !== plan.timeMs || frame.sceneId !== (plan.sceneId ?? null)
            || frame.pixel.file !== `pixels/frame-${String(index).padStart(3, '0')}.png`
            || stablePixelContext(frame.pixel.renderDetails) !== context
            || (frame.screen.cursorMode.isDefault && defaultCursorPolicy(frame.screen, frame.pixel.renderDetails) !== defaultPolicy)
            || (index === 0 ? frame.barrier !== null : !/^\x1b\[\?[\d;]+c$/.test(frame.barrier ?? ''))) throw new Error('Invalid pixel journal order or binding.');
        return retainedPixel(output, frame);
    });
    const bind = frames[1].screen;
    if (bind.lines[0] !== `NTILDE_PX_${runId}` || bind.cursorRow !== 0 || bind.cursorCol !== 42) throw new Error('Invalid pixel nonce binding.');
    return images;
}

/** Supplemental local comparisons, never stock collector grades or desktop/timing qualification. */
export function buildPixelReport(frames, runId, output) {
    const images = validatePixelFrames(frames, runId, output);
    const grid = frames[0].pixel.renderDetails.grid;
    const entries = new Map();
    frames.forEach((frame, index) => { if (frame.sceneId) entries.set(`${frame.sceneId}:${frame.pixel.presentationTimeMs}`, { frame, image: images[index] }); });
    const region = (id, area = pixelRegion, time = 0) => regionPixels(entries.get(`${id}:${time}`).image, grid, area);
    const normal = region('normal'), blank = region('normal', blankRegion), control = region('normal', controlRegion);
    const content = id => sceneContentReady(entries.get(`${id}:0`).frame);
    const ready = content('normal') && ink(normal) > 10 && solid(blank, [16, 16, 16, 255]) && normal.equals(control);
    const observations = [];
    const add = (featureId, controls, assertions, snapshots) => observations.push({ featureId, snapshots,
        controls, assertions, result: controls.every(item => item.passed) ? (assertions.every(item => item.passed) ? 'passed' : 'failed') : 'inconclusive' });
    const check = (description, passed) => ({ description, passed });
    const targetIndex = id => entries.get(`${id}:0`).frame.index;
    const common = id => [check('target text and cursor match fixture input', content(id)), check('normal glyphs and blank positive controls', ready), check('non-target control unchanged', region(id, controlRegion).equals(control))];
    for (const id of ['bold', 'italic', 'faint', 'hidden', 'reverse', 'strike', 'overline', ...[1, 2, 3, 4, 5].map(style => `underline.${style}`)]) {
        const target = region(id), empty = region(id, blankRegion);
        let assertions;
        if (id === 'hidden') assertions = [check('glyph ink suppressed', solid(target, [16, 16, 16, 255])), check('background retained', empty.equals(blank))];
        else if (id === 'reverse') assertions = [check('foreground/background exchange includes blank cells', solid(empty, [240, 240, 240, 255])), check('glyph region changes', changed(target, normal))];
        else if (['strike', 'overline'].includes(id)) assertions = [check('decoration changes glyph and blank cells', changed(target, normal) && changed(empty, blank))];
        else if (id.startsWith('underline.')) assertions = [check('independent green decoration visible on blank cells',
            countColorCoverage(empty, [32, 224, 96, 255]) > 0), check('glyph region changes', changed(target, normal))];
        else assertions = [check('glyph pixels differ with attribute', changed(target, normal)), check('blank cells unchanged', empty.equals(blank))];
        add(`rendition.${id}`, common(id), assertions, [targetIndex('normal'), targetIndex(id)]);
    }
    const underlines = [1, 2, 3, 4, 5].map(style => region(`underline.${style}`, blankRegion));
    add('underline.distinct-styles', [check('all underline inputs match the recorded text/cursor', [1, 2, 3, 4, 5].every(style => content(`underline.${style}`))), check('all five blank-cell controls carry green underline pixels', underlines.every(bytes => countColorCoverage(bytes, [32, 224, 96, 255]) > 0))],
        [check('five local underline masks are distinct', new Set(underlines.map(bytes => bytes.toString('hex'))).size === 5)], [1, 2, 3, 4, 5].map(style => targetIndex(`underline.${style}`)));
    const palette = entries.get('colors:0'), paletteAssertions = [];
    for (let index = 0; index < 16; index++) {
        const wanted = colorBytes(palette.frame.pixel.renderDetails.theme.ansiPaletteArgb[index]);
        for (const row of [2, 4]) {
            const pixels = regionPixels(palette.image, grid, { row, col: 2 + index * 2, rows: 1, cols: 2 });
            paletteAssertions.push(check(`palette ${index} ${row === 2 ? 'background' : 'full-block foreground'} interior`, countColor(pixels, wanted) > pixels.length / 8));
        }
    }
    for (const col of [2, 6]) paletteAssertions.push(check(`RGB ${col === 2 ? 'background' : 'foreground'} interior`,
        countColor(regionPixels(palette.image, grid, { row: 6, col, rows: 1, cols: 4 }), [23, 91, 173, 255]) > grid.cellWidthPx * grid.cellHeightPx));
    add('colors.palette-and-rgb', [check('color patches match the recorded text/cursor', content('colors')), check('palette has multiple distinguishable entries', new Set(palette.frame.pixel.renderDetails.theme.ansiPaletteArgb.slice(0, 16)).size > 8)], paletteAssertions, [palette.frame.index]);
    const blink = [0, 529, 530, 1059, 1060].map(time => region('text-blink', pixelRegion, time));
    add('blink.text-phases', [check('identical input text/cursor at all phases', [0, 529, 530, 1059, 1060].every(time => sceneContentReady(entries.get(`text-blink:${time}`).frame))), check('visible glyphs and decorations present', ink(blink[0]) > 10),
        check('non-target control stable at all phases', [0, 529, 530, 1059, 1060].every(time => region('text-blink', controlRegion, time).equals(control)))],
        [check('same input visible at 0/529/1060ms', blink[0].equals(blink[1]) && blink[0].equals(blink[4])),
            check('glyphs and decorations absent at 530/1059ms with background retained', solid(blink[2], [16, 16, 16, 255]) && blink[2].equals(blink[3])
                && solid(region('text-blink', blankRegion, 530), [16, 16, 16, 255]))], frames.filter(frame => frame.sceneId === 'text-blink').map(frame => frame.index));
    const cursorOff = region('normal', cursorRegion);
    for (const parameter of ['default', 0, 1, 2, 3, 4, 5, 6, 'hidden']) {
        const id = `cursor.${parameter}`, on = region(id, cursorRegion), off = region(id, cursorRegion, 530);
        const phase = entries.get(`${id}:0`).frame.pixel.renderDetails.presentation;
        const hidden = parameter === 'hidden';
        const stored = entries.get(`${id}:0`).frame.screen;
        const wanted = expectedCursorMode(parameter);
        add(id, [check('target text and cursor match fixture input', content(id)), check('normal glyph positive control', ready), check('non-cursor glyph control unchanged', region(id).equals(normal))],
            [check('stored shape/blink/default/visibility match the exact requested mode', Object.keys(wanted).every(key => stored.cursorMode[key] === wanted[key]) && stored.cursorVisible === !hidden),
                check('visible-phase cursor agrees with visibility control', hidden ? on.equals(cursorOff) : changed(on, cursorOff)),
                check('off-phase follows effective blinking mode', hidden || phase.cursorBlinkEnabled ? off.equals(cursorOff) : off.equals(on))],
            [entries.get(`${id}:0`).frame.index, entries.get(`${id}:530`).frame.index]);
    }
    const masks = [2, 4, 6].map(parameter => region(`cursor.${parameter}`, cursorRegion));
    add('cursor.distinct-shapes', [check('steady cursor positive controls differ from hidden', masks.every(bytes => changed(bytes, cursorOff)))],
        [check('block, underline and beam masks differ', new Set(masks.map(bytes => bytes.toString('hex'))).size === 3)], [2, 4, 6].map(parameter => targetIndex(`cursor.${parameter}`)));
    return { schemaVersion: 1, suiteId: pixelSuiteId, runId, complete: true,
        scope: 'Owned offscreen renderer pixels at synthetic 530ms half-period phases; local controls and retained primary font/context. No live desktop cadence, cross-platform font/fallback equivalence, clipboard, images or stock grades.',
        inputs: pixelScenes.map(item => ({ ...item, sha256: snapshotHash(item) })),
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, snapshots: frames, observations };
}
