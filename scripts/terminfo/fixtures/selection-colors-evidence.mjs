// Synthetic, fully valid selection-color evidence for portable tests. A small App simulator interprets each experiment's stages with the
// suite's independent selection-color model, then builds every frame (stored cells, resolved colors, override metadata, render theme with
// the nullable selection sources and a PNG whose bytes depend on what the render shows) through the production validators. `behavior`
// injects the defects the grader must catch: an override that never applies, a reset that is ignored or hits the wrong channel, a second
// value rejected as stale, a null default substituted with a literal RGB, false silence under an active override, the unaffected override
// lost at the other channel's reset, a wrong-theme restoration, stale or baseline-repeated renders and wrong draw colors.
// The model checked here was run against the C# VT separately; the frozen real-App publication is the evidence.
import { createHash } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { screenSnapshot, snapshotHash } from '../agent-observer.mjs';
import { planFeeds, planReads } from '../graded-plan.mjs';
import { selectionColorsContracts, selectionColorsFixture, selectionColorsObservation, gradeSelectionColorsCase, selectionColorsModel, selectionColorsReadPlan } from '../selection-colors-contracts.mjs';
import { opaqueArgb, channels16, overrideIndices } from '../selection-colors-model.mjs';
import { replaySelectionColorQuery, replaySelectionColorResetTermless, renderSamples, buildSelectionColorsGradingReport } from '../selection-colors-grading.mjs';
import { selectionColorsAdapterId } from '../selection-colors-grading-collector.mjs';
import { syntheticColEditScreen, colEditRows, colEditCols, paletteBinding, colorCell, plainCell } from './col-edit-evidence.mjs';
import { paletteChannel, literalChannel } from './sgr-palette-evidence.mjs';
import { encodePng, syntheticRenderDetails, cellWidth, cellHeight } from './cursor-qual-evidence.mjs';

const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';
const width = colEditCols * cellWidth, height = colEditRows * cellHeight;
const baselineCursor = 0xffe5e5e5, baselineCursorText = 0xff000000;
const hostMode = { style: 2, blink: true, isDefault: true };
/** The selection paint the synthetic theme derives when no override is set (the accent composite, opaque), and the paint an active
 * override paints with. Only self-consistency with the model is claimed; the real derivation is App rendering. */
const derivedSelectionPaint = 0xff14283d;
const textRow = [1, 3, 4, 5];

/** The synthetic baseline colors (the canonical palette, a light-gray foreground, black background and a light cursor); the selection
 * sources are unset (`null`) exactly as the default theme defines them. */
export function syntheticSelectionBaseline() {
    const binding = paletteBinding();
    return { foreground: binding.foregroundArgb, background: binding.backgroundArgb, cursor: baselineCursor, palette: [...binding.ansiPaletteArgb],
        selectionBackground: null, selectionForeground: null };
}

/** The model clone of one stage's overrides. */
function overridesClone(state) {
    return { palette: new Map(state.palette), foreground: state.foreground, background: state.background, cursor: state.cursor,
        selectionBackground: state.selectionBackground, selectionForeground: state.selectionForeground };
}
/** The reset-defect bends of `appOverrides` (undefined = no bend). */
function resetDefect(spec, index, states, behavior) {
    const step = spec.stages[index];
    if (step.kind !== 'reset' || index === 0) return undefined;
    if (behavior.ignoreReset && spec.caseId.includes(behavior.ignoreReset)) return 'ignore';
    if (behavior.wrongTarget && spec.id === 'independence') return 'wrong-target';
    if (behavior.lostOther && spec.id === 'independence') return 'lost-other';
    return undefined;
}
/** The App's state after one stage under `behavior`: the model's overrides, bent by the injected defect. */
function appOverrides(spec, index, behavior) {
    const states = selectionColorsModel.overridesByStageOf(spec);
    const clone = overridesClone(states[index]);
    const step = spec.stages[index];
    const overrideAt = spec.stages.findIndex(item => item.kind === 'override' || item.kind === 'second');
    const lastOverride = () => (overrideAt > 0 ? states[overrideAt] : states[0]);
    if (behavior.neverApplied && step.kind === 'override') {
        return { palette: new Map(), foreground: null, background: null, cursor: null, selectionBackground: null, selectionForeground: null };
    }
    if (step.kind === 'second' && behavior.staleSecondValue) return lastOverride();
    const defect = resetDefect(spec, index, states, behavior);
    if (defect === 'ignore') return lastOverride();
    if (defect === 'wrong-target') {
        // The reset hits the other selection channel instead of its own: mine stays, the other is dropped.
        const mine = spec.stages[index].writes.some(bytes => bytes.startsWith('\x1b]117')) ? 'selectionBackground' : 'selectionForeground';
        const other = mine === 'selectionBackground' ? 'selectionForeground' : 'selectionBackground';
        clone[mine] = states[index - 1][mine];
        clone[other] = null;
        return clone;
    }
    if (defect === 'lost-other') {
        clone.selectionBackground = null;
        clone.selectionForeground = null;
    }
    return clone;
}

/** One reported color source: the override verbatim, else the baseline value — substituted with black under the null-to-literal defect. */
function reportedColor(baselineValue, override, defect, behavior) {
    if (override !== null) return opaqueArgb(override);
    if (behavior.nullToLiteral && defect) return 0xff000000;
    return baselineValue;
}

/** The colors the App resolves under overrides, including the null-to-literal defect (an unset selection source reported as black). */
function appColors(baseline, overrides, behavior) {
    return { foreground: reportedColor(baseline.foreground, overrides.foreground, false, behavior),
        background: reportedColor(baseline.background, overrides.background, false, behavior),
        cursor: reportedColor(baseline.cursor, overrides.cursor, false, behavior),
        selectionBackground: reportedColor(baseline.selectionBackground, overrides.selectionBackground, true, behavior),
        selectionForeground: reportedColor(baseline.selectionForeground, overrides.selectionForeground, true, behavior),
        palette: baseline.palette.map((color, slot) => (overrides.palette.has(slot) ? opaqueArgb(overrides.palette.get(slot)) : color)) };
}

/** The cell specs of one stage's screen rows: the scene, plus the pinned `X` of the reset cases. */
function stageRows(spec, index) {
    const blank = () => Array.from({ length: 4 }, () => plainCell(' '));
    const text = cell => [...selectionColorsFixture.sceneText].map(char => colorCell(char, cell));
    const rows = [];
    rows[1] = [...blank(), ...text({ fg: literalChannel(240, 240, 240), bg: literalChannel(16, 16, 16) })];
    rows[3] = [...blank(), ...text({})];
    rows[4] = [...blank(), ...text({ fg: paletteChannel(33), bg: paletteChannel(34) })];
    rows[5] = [...blank(), ...text({ fg: literalChannel(240, 240, 240), bg: literalChannel(16, 32, 48) })];
    if (spec.stages.slice(0, index + 1).some(step => step.writes.some(bytes => bytes.endsWith('X')))) rows[0] = [colorCell('X', {})];
    return rows;
}

/** The cursor position of one stage: its experiment's home, advanced past the pinned `X` once written. */
function stageCursor(spec, index) {
    const written = spec.stages.slice(0, index + 1).some(step => step.writes.some(bytes => bytes.endsWith('X')));
    return [spec.cursor[0], spec.cursor[1] + (written ? 1 : 0)];
}

/** A complete screen of one stage. The binding carries the override flags the App reports. */
function stageScreen(spec, index, colors, overrides) {
    const binding = { ...paletteBinding({ palette: colors.palette, foreground: colors.foreground, background: colors.background }),
        foregroundOverride: overrides.foreground !== null, backgroundOverride: overrides.background !== null, paletteOverrideIndices: overrideIndices(overrides) };
    const screen = syntheticColEditScreen(stageRows(spec, index), stageCursor(spec, index), undefined, [], binding);
    return screenSnapshot({ ...screen, cursorVisible: false, cursorMode: { style: 2, blink: true, isDefault: true } },
        true, true, true, true, false, true);
}

/** The render theme details: the sources (selection sources nullable, black-substituted under the defect) and the derived paints. */
function renderTheme(baseline, colors, overrides, behavior) {
    return { foregroundArgb: colors.foreground, backgroundArgb: colors.background, cursorArgb: colors.cursor,
        cursorTextArgb: overrides.cursor === null ? baselineCursorText : 0xff202020, ansiPaletteArgb: [...colors.palette],
        foregroundOverride: overrides.foreground !== null, backgroundOverride: overrides.background !== null, cursorOverride: overrides.cursor !== null,
        selectionBackgroundOverride: overrides.selectionBackground !== null, selectionForegroundOverride: overrides.selectionForeground !== null,
        selectionBackgroundArgb: reportedColor(baseline.selectionBackground, overrides.selectionBackground, true, behavior),
        selectionForegroundArgb: reportedColor(baseline.selectionForeground, overrides.selectionForeground, true, behavior),
        effectiveSelectionBackgroundArgb: colors.selectionBackground ?? derivedSelectionPaint,
        effectiveSelectionForegroundArgb: colors.selectionForeground,
        paletteOverrideIndices: overrideIndices(overrides) };
}

/** A render of one screen under one theme: a background fill, the unselected control row (literal colors, fixed), the selected band
 * (rows 2..4) painted with the effective selection paint and its inks, and (in the top pixel row, where nothing is sampled) a digest of
 * the whole state, so any state change the scene does not draw still changes the bytes. Row `r` spans y `r*cellHeight..(r+1)*cellHeight`. */
function drawRender(screen, theme) {
    const key = createHash('sha256').update(snapshotHash(screen) + snapshotHash(theme)).digest();
    const pixels = Buffer.alloc(width * height * 4);
    const paint = (x, y, argb) => { const at = (y * width + x) * 4; pixels[at] = (argb >>> 16) & 255; pixels[at + 1] = (argb >>> 8) & 255; pixels[at + 2] = argb & 255; pixels[at + 3] = 255; };
    const fill = (left, top, right, bottom, argb) => { for (let y = top; y < bottom; y++) for (let x = left; x < right; x++) paint(x, y, argb); };
    const textRow = (row, fillArgb, inkArgb) => {
        fill(4 * cellWidth, row * cellHeight, width, (row + 1) * cellHeight, fillArgb);
        for (let index = 0; index < 7; index++) fill((4 + index) * cellWidth + 1, row * cellHeight + 4, (4 + index) * cellWidth + 6, (row + 1) * cellHeight - 4, inkArgb);
    };
    fill(0, 0, width, height, theme.backgroundArgb);
    for (let at = 0; at < key.length; at++) paint(at, 0, 0xff000000 | (key[at] << 16) | (key[at] << 8) | key[at]);
    const selectionPaint = theme.effectiveSelectionBackgroundArgb;
    const selectedInk = theme.selectionForegroundOverride ? theme.selectionForegroundArgb : theme.foregroundArgb;
    textRow(1, 0xff101010, 0xf0f0f0);
    fill(4 * cellWidth, 2 * cellHeight, width, 3 * cellHeight, selectionPaint);
    textRow(3, selectionPaint, selectedInk);
    textRow(4, selectionPaint, theme.ansiPaletteArgb[33]);
    textRow(5, 0xff102030, 0xf0f0f0);
    return { width, height, pixels };
}

const reply = (spec, argb) => {
    if (argb === null || argb === undefined) return null;
    const raw = `\x1b]${spec.code};${spec.code === 4 ? spec.index + ';' : ''}rgb:${channels16(argb).map(value => value.toString(16).padStart(4, '0')).join('/')}\x1b\\`;
    return { raw, channels16: channels16(argb) };
};

const queryColor = (colors, spec) => spec.code === 4 ? colors.palette[spec.index]
    : colors[{ 10: 'foreground', 11: 'background', 12: 'cursor', 17: 'selectionBackground', 19: 'selectionForeground' }[spec.code]];

const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const pixelOf = (index, screen, theme, png) => {
    const details = syntheticRenderDetails(screen, 0, width, height);
    return { file: `selc-pixels/frame-${String(index).padStart(3, '0')}.png`, sha256: sha(png), byteCount: png.length, width, height, rows: colEditRows, cols: colEditCols,
        mode: 'render', scale: 1, downscaled: false, inlineOmitted: false, presentationTimeMs: 0,
        syntheticSelection: structuredClone(selectionColorsFixture.selectionColorsSelection),
        renderDetails: { ...details, theme, syntheticSelection: structuredClone(selectionColorsFixture.selectionColorsSelection) } };
};
/** One journaled frame; `view` is `{ theme, extra, repeat, drawn }`: the render theme, the read's case/stage/replies, a frame whose render is
 * repeated (a stale render) and a theme the renderer drew differently from the one it reported. */
const frame = (index, kind, screen, barrier, view, { pngs, images }) => {
    const { theme, extra = {}, repeat = null, drawn = null } = view;
    const image = repeat === null ? drawRender(screen, drawn ?? theme)
        : images.get(repeat.pixel.file);
    const png = repeat === null ? encodePng(image) : pngs.get(repeat.pixel.file);
    const pixel = pixelOf(index, screen, theme, png);
    pngs.set(pixel.file, png); images.set(pixel.file, image);
    return { index, kind, paneId, barrier, screen, sha256: snapshotHash(screen), afterScreen: structuredClone(screen), afterSha256: snapshotHash(screen),
        caseId: null, experimentId: null, stageId: null, queries: [], ...extra, pixel };
};

/** The pristine host screen every run starts and finishes on. */
function hostScreen(colors = syntheticSelectionBaseline()) {
    const binding = paletteBinding({ palette: colors.palette, foreground: colors.foreground, background: colors.background });
    return screenSnapshot({ ...syntheticColEditScreen([], [0, 0], undefined, [], binding), cursorVisible: true, cursorMode: hostMode }, true, true, true, true, false, true);
}

/** The replies one stage's queries get: absent when the behavior silences them, else the model colors (stale at a reset when asked to be). */
function stageQueries(spec, step, states, colors, baseline, behavior) {
    const overrideAt = spec.stages.findIndex(item => item.kind === 'override');
    const stale = behavior.staleReplies && step.kind === 'reset' && overrideAt > 0;
    const shown = stale ? appColors(baseline, states[overrideAt], {}) : colors;
    const silent = behavior.noReplies || (behavior.falseSilence && (step.kind === 'override' || step.kind === 'second'));
    return spec.queries.map(item => {
        const silenced = silent && [17, 19].includes(item.code);
        return { spec: item, barrier: deviceAttribute, reply: silenced ? null : reply(item, queryColor(shown, item)) };
    });
}

/** One stage's frame and read record, pushed onto `frames`; returns the read record. */
function stageFrameAndRead(ctx) {
    const { contract, spec, index, step, states, firstFrame, frames, store, baseline, behavior } = ctx;
    const overrides = appOverrides(spec, index, behavior), colors = appColors(baseline, overrides, behavior);
    const screen = stageScreen(spec, index, colors, overrides);
    const queries = stageQueries(spec, step, states, colors, baseline, behavior);
    let repeat = null;
    if (behavior.renderStale && step.kind === 'reset') repeat = frames.at(-1);
    else if (behavior.baselinePng && step.kind === 'reset') repeat = frames[firstFrame];
    let drawn = behavior.wrongRender && step.kind === 'reset'
        ? { ...renderTheme(baseline, colors, overrides, behavior), effectiveSelectionBackgroundArgb: 0xff123456 } : undefined;
    // A renderer that draws the default ink whenever a selection background override is also active.
    if (behavior.inkIgnoresForegroundUnderBackground && overrides.selectionBackground !== null && overrides.selectionForeground !== null) {
        drawn = { ...renderTheme(baseline, colors, overrides, behavior), selectionForegroundOverride: false };
    }
    // A renderer that keeps drawing the foreground it was given after that foreground was reset while a background override remains.
    if (behavior.staleInkAfterReset && step.kind === 'reset' && overrides.selectionBackground !== null && overrides.selectionForeground === null) {
        const before = appOverrides(spec, index - 1, behavior);
        drawn = { ...renderTheme(baseline, colors, overrides, behavior), selectionForegroundOverride: true,
            selectionForegroundArgb: renderTheme(baseline, appColors(baseline, before, behavior), before, behavior).selectionForegroundArgb };
    }
    // A renderer that paints the selected text in the selection fill's own color when only a background override is active.
    if (behavior.invisibleInkUnderBackground && overrides.selectionBackground !== null && overrides.selectionForeground === null) {
        drawn = { ...renderTheme(baseline, colors, overrides, behavior), selectionForegroundOverride: true,
            selectionForegroundArgb: renderTheme(baseline, colors, overrides, behavior).selectionBackgroundArgb };
    }
    const view = { theme: renderTheme(baseline, colors, overrides, behavior), extra: { caseId: contract.caseId, experimentId: spec.id, stageId: step.id, queries },
        repeat, drawn };
    frames.push(frame(frames.length, 'selc-read', screen, deviceAttribute, view, store));
    return { kind: 'state', cursor: [screen.cursorRow, screen.cursorCol], queries: structuredClone(queries) };
}

/** The frames and worker reads of one case. */
function caseFrames(contract, frames, store, baseline, behavior) {
    const reads = [];
    for (const spec of contract.experiments) {
        const firstFrame = frames.length;
        const states = selectionColorsModel.overridesByStageOf(spec);
        for (const [index, step] of spec.stages.entries()) {
            reads.push(stageFrameAndRead({ contract, spec, index, step, states, firstFrame, frames, store, baseline, behavior }));
        }
    }
    return reads;
}

/** A complete, valid synthetic run: frames, worker run log, adapter facts and the retained PNGs. `behavior` builds negative evidence. */
export function syntheticSelectionColorsEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), behavior = {}) {
    const baseline = syntheticSelectionBaseline(), pngs = new Map(), images = new Map(), store = { pngs, images };
    const host = hostScreen(), hostOverrides = { palette: new Map(), foreground: null, background: null, cursor: null, selectionBackground: null, selectionForeground: null };
    const hostTheme = renderTheme(baseline, appColors(baseline, hostOverrides, {}), hostOverrides, {});
    const nonce = 'NTILDE_SL_' + runId;
    const frames = [frame(0, 'selc-start', host, null, { theme: hostTheme }, store)];
    frames.push(frame(1, 'selc-bind', { ...host, lines: [nonce, ...host.lines.slice(1)], cursorCol: nonce.length }, deviceAttribute, { theme: hostTheme }, store));
    const cases = selectionColorsContracts.map(contract => {
        const reads = caseFrames(contract, frames, store, baseline, behavior);
        const feeds = planFeeds(contract);
        const pinned = contract.isResetCase
            ? { probeId: contract.caseId, ...replaySelectionColorResetTermless(contract, frames[2 + positionOf(contract) + contract.roles.indexOf(contract.pinnedRoles[0])].screen) }
            : { probeId: contract.caseId, ...replaySelectionColorQuery(contract, [reads[contract.roles.indexOf(contract.pinnedRoles[0])].queries[0].reply]) };
        return { caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute), reads, pinned };
    });
    function positionOf(contract) {
        let before = 0;
        for (const item of selectionColorsContracts) {
            if (item.caseId === contract.caseId) break;
            before += planReads(item);
        }
        return before;
    }
    const finishScreen = behavior.restorationDrift
        ? screenSnapshot({ ...host, lines: ['drifted', ...host.lines.slice(1)] }, true, true, true, true, false, true) : host;
    frames.push(frame(frames.length, 'selc-finish', finishScreen, deviceAttribute, { theme: hostTheme }, store));
    const run = { schemaVersion: 1, adapterId: selectionColorsAdapterId, probeIds: selectionColorsContracts.map(item => item.caseId), cases };
    const adapter = { id: selectionColorsAdapterId, derivedFromAdapterId: 'ntilde-owned-osc-title-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, pngs, images, captureOf: item => ({ details: item.pixel.renderDetails, image: images.get(item.pixel.file) }) };
}

/** Write the retained PNGs of one synthetic run under `output/selc-pixels`, as the real observer does. */
export function writeSelectionColorsPngs(evidence, output) {
    mkdirSync(join(output, 'selc-pixels'), { recursive: true });
    for (const [file, bytes] of evidence.pngs) writeFileSync(join(output, file), bytes);
}

/** The independent contract verdict of every case of a synthetic run. Render samples compare each read frame
 * against its own experiment's first read frame (the experiment's baseline), exactly as the report builder does. */
export function syntheticSelectionColorsGrades(evidence) {
    const baselineOfExperiment = new Map();
    for (let index = 2; index < evidence.frames.length - 1; index++) {
        const entry = selectionColorsReadPlan[index - 2];
        const key = `${entry.caseId}/${entry.experimentId}`;
        if (!baselineOfExperiment.has(key)) baselineOfExperiment.set(key, evidence.frames[index]);
    }
    let at = 2;
    return selectionColorsContracts.map(contract => {
        const readFrames = evidence.run.cases.find(item => item.caseId === contract.caseId).reads.map(() => evidence.frames[at++]);
        const observation = selectionColorsObservation(contract, readFrames.map(frame => ({ screen: frame.screen, queries: frame.queries,
            details: frame.pixel.renderDetails, pixelSha256: frame.pixel.sha256, render: renderSamples(evidence.images.get(frame.pixel.file),
                frame.pixel.renderDetails, evidence.images.get(baselineOfExperiment.get(`${frame.caseId}/${frame.experimentId}`).pixel.file)) })));
        return gradeSelectionColorsCase(contract, observation);
    });
}

export function syntheticSelectionColorsReports(evidence, options = {}) {
    const frames = structuredClone(evidence.frames);
    const run = structuredClone(evidence.run);
    return buildSelectionColorsGradingReport(frames, run, evidence.runId, structuredClone(evidence.adapter), {
        captureOf: frame => ({ details: frame.pixel.renderDetails, image: evidence.images.get(frame.pixel.file) }), ...options });
}

/** A deep copy of the mutable parts of a run (journal, run log, adapter), sharing the retained PNG bytes. */
export const cloneSelectionColorsEvidence = evidence => ({ ...evidence, frames: structuredClone(evidence.frames), run: structuredClone(evidence.run), adapter: structuredClone(evidence.adapter) });
