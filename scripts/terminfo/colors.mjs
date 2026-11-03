import { screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { colorCases, colorPlan, colorSuiteId, overrideBlue, overrideGreen } from './color-cases.mjs';
import { validateColorCapture, stableColorContext, validateQueries, pristineColorTheme } from './color-capture.mjs';
import { retainedColorCaptureResult, totalPixelLimit, defaultCursorPolicy } from './pixels-capture.mjs';
import { pristineCursorBaseline } from './cursor.mjs';
import { regionPixels, countColorCoverage } from './pixels.mjs';
import { pixelRegion, blankRegion, controlRegion, cursorRegion } from './pixels-cases.mjs';
const check = (description, passed) => ({ description, passed });
const rgba = argb => [(argb >>> 16) & 255, (argb >>> 8) & 255, argb & 255, argb >>> 24];
function queryColor(theme, spec) {
    if (spec.code === 4) return theme.ansiPaletteArgb[spec.index];
    return theme[({ 10: 'foregroundArgb', 11: 'backgroundArgb', 12: 'cursorArgb', 17: 'selectionBackgroundArgb', 19: 'selectionForegroundArgb' })[spec.code]];
}
function queryMatches(item, theme) {
    const value = queryColor(theme, item.spec);
    if (value === null) return item.reply === null;
    return item.reply !== null && item.reply.channels16.every((channel, index) => channel === rgba(value)[index] * 257);
}
function expectedTheme(item, stage, baseline) {
    const theme = structuredClone(baseline);
    if (stage === 'baseline' || stage === 'reset') return theme;
    for (const spec of item.queries) {
        if (stage === 'partial-reset' && ((item.id.endsWith('117') && spec.code === 17) || (item.id.endsWith('119') && spec.code === 19))) continue;
        const value = spec.code === 19 && item.id.startsWith('selection.independent') || spec.index === 200 && item.id === 'palette.all' ? overrideGreen : overrideBlue;
        if (spec.code === 4) { theme.ansiPaletteArgb[spec.index] = value; theme.paletteOverrideIndices.push(spec.index); }
        else {
            const key = ({ 10: 'foreground', 11: 'background', 12: 'cursor', 17: 'selectionBackground', 19: 'selectionForeground' })[spec.code];
            theme[key + 'Argb'] = value; theme[key + 'Override'] = true;
        }
    }
    return theme;
}
function themeSources(theme) {
    const sources = { ...theme };
    delete sources.effectiveSelectionForegroundArgb;
    delete sources.effectiveSelectionBackgroundArgb;
    delete sources.cursorTextArgb;
    return sources;
}
function themeMatches(actual, expected) {
    return snapshotHash(themeSources(actual)) === snapshotHash(themeSources(expected));
}
function targetRegion(id) {
    if (id === 'cursor') return cursorRegion;
    if (['background', 'selection-background'].includes(id)) return blankRegion;
    return pixelRegion;
}
function partialPaintMatches(resetBg, baseline, override, partial, region) {
    const blank = region(partial, blankRegion);
    const wantedBlank = region(resetBg ? baseline : override, blankRegion);
    const theme = baseline.pixel.renderDetails.theme;
    const foreground = resetBg ? overrideGreen : theme.effectiveSelectionForegroundArgb ?? theme.foregroundArgb;
    return blank.equals(wantedBlank)
        && countColorCoverage(region(partial, pixelRegion), rgba(foreground), [...wantedBlank.subarray(0, 4)]) > 10;
}
function contentReady(frame, item) {
    const lines = new Array(frame.screen.rows).fill('');
    if (item.id.startsWith('palette.')) lines[4] = '    ███████';
    else lines[2] = '    HMWqg_|';
    lines[6] = '    HMWqg_|';
    return snapshotHash(lines) === snapshotHash(frame.screen.lines) && frame.screen.cursorRow === 8 && frame.screen.cursorCol === 8
        && frame.screen.cursorVisible === (item.id === 'cursor');
}
export function validateColorFrames(frames, runId, output) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '') || !Array.isArray(frames) || frames.length !== colorPlan.length + 3) throw new Error('Incomplete color journal.');
    if (frames.some(frame => !Number.isSafeInteger(frame.pixel?.width) || !Number.isSafeInteger(frame.pixel?.height) || frame.pixel.width < 1 || frame.pixel.height < 1)
        || frames.reduce((sum, frame) => sum + frame.pixel.width * frame.pixel.height, 0) > totalPixelLimit) throw new Error('Color pixel budget exhausted.');
    const first = frames[0], last = frames.at(-1), context = stableColorContext(first.pixel.renderDetails);
    if (!pristineCursorBaseline(first.screen) || first.screen.cellDetails.some(cell => cell.overline) || !pristineColorTheme(first.pixel.renderDetails.theme)
        || snapshotHash(first.screen) !== snapshotHash(last.screen) || first.pixel.sha256 !== last.pixel.sha256
        || snapshotHash(first.pixel.renderDetails.theme) !== snapshotHash(last.pixel.renderDetails.theme)) throw new Error('Color restoration was not verified.');
    const images = frames.map((frame, index) => {
        const plan = index >= 2 && index < frames.length - 1 ? colorPlan[index - 2] : { selection: null, queries: [] };
        let kind = 'color-read';
        if (index === 0) kind = 'color-start';
        else if (index === 1) kind = 'color-bind';
        else if (index === frames.length - 1) kind = 'color-finish';
        if (frame.index !== index || frame.kind !== kind || frame.paneId !== first.paneId || !/^[a-f\d-]{36}$/i.test(frame.paneId ?? '')
            || frame.sha256 !== snapshotHash(screenSnapshot(frame.screen, false, true, true, true)) || frame.afterSha256 !== frame.sha256
            || snapshotHash(screenSnapshot(frame.afterScreen, false, true, true, true)) !== frame.sha256
            || frame.caseId !== (plan.caseId ?? null) || frame.stageId !== (plan.stageId ?? null)
            || frame.screen.rows !== first.screen.rows || frame.screen.cols !== first.screen.cols
            || stableColorContext(frame.pixel.renderDetails) !== context
            || frame.pixel.file !== `color-pixels/frame-${String(index).padStart(3, '0')}.png`
            || (frame.screen.cursorMode.isDefault && defaultCursorPolicy(frame.screen, frame.pixel.renderDetails) !== defaultCursorPolicy(first.screen, first.pixel.renderDetails))
            || (index === 0 ? frame.barrier !== null : !/^\x1b\[\?[\d;]+c$/.test(frame.barrier ?? ''))) throw new Error('Invalid color journal binding.');
        validateQueries(frame.queries, plan.queries);
        return validateColorCapture(retainedColorCaptureResult(output, frame), frame.screen, plan.selection).image;
    });
    const bind = frames[1].screen;
    if (bind.lines[0] !== `NTILDE_CL_${runId}` || bind.cursorRow !== 0 || bind.cursorCol !== 42) throw new Error('Invalid color nonce.');
    return images;
}
export function buildColorReport(frames, runId, output) {
    const images = validateColorFrames(frames, runId, output), grid = frames[0].pixel.renderDetails.grid;
    const observations = colorCases.map(item => {
        const samples = frames.filter(frame => frame.caseId === item.id), baseline = samples[0], last = samples.at(-1);
        const region = (frame, area) => regionPixels(images[frame.index], grid, area);
        const controls = [check('case baseline equals the pristine private host theme', snapshotHash(baseline.pixel.renderDetails.theme) === snapshotHash(frames[0].pixel.renderDetails.theme)),
            check('retained text, cursor and fixture match at every stage', samples.every(frame => contentReady(frame, item))),
            check('unselected explicit RGB control has visible ink', countColorCoverage(region(baseline, controlRegion), [240, 240, 240, 255]) > 10),
            check('unselected explicit RGB control stays unchanged', samples.every(frame => region(frame, controlRegion).equals(region(baseline, controlRegion))))];
        const assertions = [];
        for (const frame of samples) {
            assertions.push(check(`${frame.stageId}: raw queries match exact 16-bit theme values or explicit unset defaults`, frame.queries.every(query => queryMatches(query, frame.pixel.renderDetails.theme))),
                check(`${frame.stageId}: only requested runtime sources change`, themeMatches(frame.pixel.renderDetails.theme, expectedTheme(item, frame.stageId, baseline.pixel.renderDetails.theme))));
        }
        assertions.push(check('reset restores complete theme including derived selection paints', snapshotHash(last.pixel.renderDetails.theme) === snapshotHash(baseline.pixel.renderDetails.theme)),
            check('reset restores identical selected/unselected full PNG', last.pixel.sha256 === baseline.pixel.sha256));
        const override = samples[1], target = targetRegion(item.id);
        assertions.push(check('override changes target pixels', !region(override, target).equals(region(baseline, target))));
        const wanted = item.id.startsWith('selection.independent') ? overrideGreen : overrideBlue;
        const background = item.id.startsWith('selection.') ? rgba(overrideBlue) : [16, 16, 16, 255];
        assertions.push(check('override target carries requested RGB coverage', countColorCoverage(region(override, target), rgba(wanted), background) > 0));
        if (samples.length === 4) {
            const partial = samples[2], resetBg = item.id.endsWith('117');
            const theme = partial.pixel.renderDetails.theme;
            assertions.push(check('partial reset retains the other independent source', resetBg ? theme.selectionForegroundOverride && !theme.selectionBackgroundOverride : theme.selectionBackgroundOverride && !theme.selectionForegroundOverride),
                check('partial reset changes selected pixels while retaining its other override', partialPaintMatches(resetBg, baseline, override, partial, region)
                    && !region(partial, pixelRegion).equals(region(override, pixelRegion)) && !region(partial, pixelRegion).equals(region(baseline, pixelRegion))));
        }
        let result = 'passed';
        if (!controls.every(value => value.passed)) result = 'inconclusive';
        else if (!assertions.every(value => value.passed)) result = 'failed';
        return { featureId: item.id, snapshots: samples.map(frame => frame.index), controls, assertions, result };
    });
    return { schemaVersion: 1, suiteId: colorSuiteId, runId, complete: true,
        scope: 'Owned runtime OSC resets, exact fenced RGB replies and synthetic selected/unselected offscreen PNGs. Unset selection defaults have no concrete reply. No live selection UI, desktop cadence, platform equivalence or stock collector regrading.',
        inputs: colorCases.map(item => ({ ...item, sha256: snapshotHash(item) })), initialSnapshot: 0, restoredSnapshot: frames.length - 1, snapshots: frames, observations };
}
