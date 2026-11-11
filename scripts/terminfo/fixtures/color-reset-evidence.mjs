// Synthetic, fully valid runtime-color reset evidence for portable tests. A small App simulator interprets each experiment's stages with the
// suite's independent color model, then builds every frame (stored cells, resolved colors, override metadata, render theme and a PNG whose
// bytes depend on what the render shows) through the production validators. `behavior` injects the defects the grader must catch: an
// override that never applies, a reset that is ignored or hits the wrong target, an individual reset that clears everything, a whole-palette
// reset that restores only part of the palette, SGR 39/49 treated as the runtime reset, a wrong-theme restoration and stale or missing replies.
// The model checked here was run against the C# VT separately; the frozen real-App publication is the evidence.
import { createHash } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { screenSnapshot, snapshotHash } from '../agent-observer.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { colorResetContracts, colorResetFixture, colorResetObservation, gradeColorResetCase, colorResetModel } from '../color-reset-contracts.mjs';
import { opaqueArgb, channels16, overrideIndices } from '../color-reset-model.mjs';
import { replayColorResetPinned, renderSamples } from '../color-reset-grading.mjs';
import { colorResetAdapterId } from '../color-reset-grading-collector.mjs';
import { syntheticColEditScreen, colEditRows, colEditCols, paletteBinding, colorCell, plainCell } from './col-edit-evidence.mjs';
import { paletteChannel, literalChannel } from './sgr-palette-evidence.mjs';
import { encodePng, syntheticRenderDetails, cellWidth, cellHeight } from './cursor-qual-evidence.mjs';

const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';
const width = colEditCols * cellWidth, height = colEditRows * cellHeight;
const baselineCursor = 0xffe5e5e5, baselineCursorText = 0xff000000;
const hostMode = { style: 2, blink: true, isDefault: true };

/** The synthetic baseline colors (the canonical palette, a light-gray foreground, black background and a light cursor). */
export function syntheticBaseline() {
    const binding = paletteBinding();
    return { foreground: binding.foregroundArgb, background: binding.backgroundArgb, cursor: baselineCursor, palette: [...binding.ansiPaletteArgb] };
}

/** The App's state after one stage under `behavior`: the model's overrides, bent by the injected defect. */
function appOverrides(spec, index, behavior) {
    const states = colorResetModel.overridesByStageOf(spec);
    const state = states[index], clone = { palette: new Map(state.palette), foreground: state.foreground, background: state.background, cursor: state.cursor };
    const step = spec.stages[index];
    const resets = step.kind === 'reset' || step.kind === 'sgr';
    if (behavior.neverApplied && step.kind === 'override') return { palette: new Map(), foreground: null, background: null, cursor: null };
    if (resets && behavior.ignoreReset && spec.caseId.includes(behavior.ignoreReset) && index > 0) return states[Math.max(1, spec.stages.findIndex(item => item.kind === 'override'))];
    if (step.kind === 'reset' && behavior.individualResetClearsAll && spec.id === 'individual-slots') clone.palette.clear();
    if (step.kind === 'reset' && behavior.wholeResetPartial && spec.id === 'whole-palette') for (let slot = 16; slot < 256; slot++) clone.palette.set(slot, colorResetFixture.slotRgb(slot));
    if (step.kind === 'reset' && behavior.wrongTarget && spec.id === 'isolation') { clone.background = null; clone.cursor = null; clone.foreground = null; clone.palette.clear(); }
    if (step.id === 'sgr' && behavior.sgr39ClearsOverride) { clone.foreground = null; clone.background = null; }
    return clone;
}

/** The colors the App resolves under overrides, including the wrong-theme defect (a reset restores a conventional, not the configured, color). */
function appColors(baseline, overrides, behavior, resetDone) {
    const colors = { foreground: overrides.foreground === null ? baseline.foreground : opaqueArgb(overrides.foreground),
        background: overrides.background === null ? baseline.background : opaqueArgb(overrides.background),
        cursor: overrides.cursor === null ? baseline.cursor : opaqueArgb(overrides.cursor),
        palette: baseline.palette.map((color, slot) => (overrides.palette.has(slot) ? opaqueArgb(overrides.palette.get(slot)) : color)) };
    if (behavior.wrongTheme && resetDone) { colors.foreground = 0xffc0c0c0; colors.background = 0xff101010; colors.palette[0] = 0xff010101; colors.cursor = 0xffc0c0c0; }
    return colors;
}

/** The cell specs of one stage's screen rows. */
function stageRows(spec, index) {
    const blank = () => [0, 1, 2, 3].map(() => plainCell(' '));
    const text = cell => [...colorResetFixture.sceneText].map(char => colorCell(char, cell));
    const rows = [];
    rows[1] = [...blank(), ...text({})];
    rows[2] = [...blank(), ...text({ fg: paletteChannel(spec.scene.fgIndex) })];
    rows[3] = [...blank(), ...text({ fg: literalChannel(240, 240, 240) })];
    rows[4] = [...blank(), ...text({ bg: paletteChannel(spec.scene.bgIndex) })];
    rows[5] = [...blank(), ...text({ bg: literalChannel(16, 32, 48) })];
    const steps = spec.stages.slice(0, index + 1);
    const code = spec.queries[0]?.code;
    const explicit = code === 11 ? { bg: paletteChannel(34) } : { fg: paletteChannel(33) };
    if (steps.some(step => step.id === 'sgr')) rows[10] = [...blank(), colorCell('A', explicit), colorCell('B', {})];
    if (steps.some(step => step.id === 'reset' && step.cells.length)) rows[12] = [...blank(), colorCell('C', explicit)];
    return rows;
}

/** The mode a scene's cursor configuration reads back as. */
const cursorOf = spec => (spec.scene.cursor === null ? { ...hostMode, visible: false, configured: false }
    : { style: spec.scene.cursor.style, blink: spec.scene.cursor.blink, isDefault: false, visible: spec.scene.cursor.visible, configured: true });

/** A complete screen of one stage. The binding carries the override flags the App reports. */
function stageScreen(spec, index, colors, overrides) {
    const binding = { ...paletteBinding({ palette: colors.palette, foreground: colors.foreground, background: colors.background }),
        foregroundOverride: overrides.foreground !== null, backgroundOverride: overrides.background !== null, paletteOverrideIndices: overrideIndices(overrides) };
    const cursor = cursorOf(spec);
    const screen = syntheticColEditScreen(stageRows(spec, index), [8, 8], undefined, [], binding);
    return screenSnapshot({ ...screen, cursorVisible: cursor.visible, cursorMode: { style: cursor.style, blink: cursor.blink, isDefault: cursor.isDefault } },
        true, true, true, true, false, true);
}

/** The render theme details: the sources and the derived cursor text and selection paint. */
function renderTheme(colors, overrides) {
    return { foregroundArgb: colors.foreground, backgroundArgb: colors.background, cursorArgb: colors.cursor,
        cursorTextArgb: overrides.cursor === null ? baselineCursorText : 0xff202020, ansiPaletteArgb: [...colors.palette],
        foregroundOverride: overrides.foreground !== null, backgroundOverride: overrides.background !== null, cursorOverride: overrides.cursor !== null,
        selectionBackgroundOverride: false, selectionForegroundOverride: false, selectionBackgroundArgb: null, selectionForegroundArgb: null,
        effectiveSelectionBackgroundArgb: 0x80336699, effectiveSelectionForegroundArgb: null, paletteOverrideIndices: overrideIndices(overrides) };
}

/** A render of one screen under one theme: a background fill, ink on the default-colored text row, a filled block cursor when one is visible, and (in the top band, where nothing is
 * sampled) a digest of the whole state, so any state change the scene does not draw still changes the bytes. */
function drawRender(screen, theme) {
    const key = createHash('sha256').update(snapshotHash(screen) + snapshotHash(theme)).digest();
    const pixels = Buffer.alloc(width * height * 4);
    const paint = (x, y, argb) => { const at = (y * width + x) * 4; pixels[at] = (argb >>> 16) & 255; pixels[at + 1] = (argb >>> 8) & 255; pixels[at + 2] = argb & 255; pixels[at + 3] = 255; };
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) paint(x, y, theme.backgroundArgb);
    for (let at = 0; at < key.length; at++) paint(at, 0, 0xff000000 | (key[at] << 16) | (key[at] << 8) | key[at]);
    if (screen.lines[1] !== '') for (let y = cellHeight + 4; y < cellHeight + 14; y++) for (let x = 4 * cellWidth; x < 11 * cellWidth; x += 2) paint(x, y, theme.foregroundArgb);
    if (screen.cursorVisible && screen.cursorMode.style === 0) {
        for (let y = 8 * cellHeight; y < 9 * cellHeight; y++) for (let x = 8 * cellWidth; x < 9 * cellWidth; x++) paint(x, y, theme.cursorArgb);
    }
    return { width, height, pixels };
}

const reply = (spec, argb) => {
    const raw = `\x1b]${spec.code};${spec.code === 4 ? spec.index + ';' : ''}rgb:${channels16(argb).map(value => value.toString(16).padStart(4, '0')).join('/')}\x1b\\`;
    return { raw, channels16: channels16(argb) };
};
const queryColor = (colors, spec) => (spec.code === 4 ? colors.palette[spec.index] : colors[{ 10: 'foreground', 11: 'background', 12: 'cursor' }[spec.code]]);

const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const pixelOf = (index, screen, theme, png) => {
    const details = syntheticRenderDetails(screen, 0, width, height);
    return { file: `crst-pixels/frame-${String(index).padStart(3, '0')}.png`, sha256: sha(png), byteCount: png.length, width, height, rows: colEditRows, cols: colEditCols,
        mode: 'render', scale: 1, downscaled: false, inlineOmitted: false, presentationTimeMs: 0, renderDetails: { ...details, theme } };
};
/** One journaled frame; `view` is `{ theme, extra, repeat }`: the render theme, the read's case/stage/replies and a frame whose render is repeated (a stale render). */
const frame = (index, kind, screen, barrier, view, { pngs, images }) => {
    const { theme, extra = {}, repeat = null, drawn = theme } = view;
    const image = repeat === null ? drawRender(screen, drawn) : images.get(repeat.pixel.file), png = repeat === null ? encodePng(image) : pngs.get(repeat.pixel.file);
    const pixel = pixelOf(index, screen, theme, png);
    pngs.set(pixel.file, png); images.set(pixel.file, image);
    return { index, kind, paneId, barrier, screen, sha256: snapshotHash(screen), afterScreen: structuredClone(screen), afterSha256: snapshotHash(screen),
        caseId: null, experimentId: null, stageId: null, queries: [], ...extra, pixel };
};

/** The pristine host screen every run starts and finishes on. */
function hostScreen(colors = syntheticBaseline()) {
    const binding = paletteBinding({ palette: colors.palette, foreground: colors.foreground, background: colors.background });
    return screenSnapshot({ ...syntheticColEditScreen([], [0, 0], undefined, [], binding), cursorVisible: true, cursorMode: hostMode }, true, true, true, true, false, true);
}

/** The replies one stage's queries get: absent when the behavior silences them, else the model colors (stale after a reset when asked to be). */
function stageQueries(spec, step, colors, baseline, behavior) {
    const stale = behavior.staleReplies && step.kind === 'reset' ? appColors(baseline, appOverrides(spec, 1, {}), {}, false) : colors;
    const silent = behavior.noReplies || (behavior.noResetReplies && step.kind === 'reset');
    return spec.queries.map(item => ({ spec: item, barrier: deviceAttribute, reply: silent ? null : reply(item, queryColor(stale, item)) }));
}

/** The render theme of a stage: stale (the override still drawn) after a reset when the behavior says the renderer never applied it. */
function stageTheme(spec, step, colors, overrides, baseline, behavior) {
    if (!(behavior.renderStale && step.kind === 'reset')) return renderTheme(colors, overrides);
    const stale = appOverrides(spec, 1, {});
    return renderTheme(appColors(baseline, stale, {}, false), stale);
}

/** The frames and worker reads of one case. */
function caseFrames(contract, frames, store, baseline, behavior) {
    const reads = [];
    for (const spec of contract.experiments) {
        const firstFrame = frames.length;
        let resetDone = false;
        for (const [index, step] of spec.stages.entries()) {
            resetDone ||= step.kind === 'reset';
            const overrides = appOverrides(spec, index, behavior), colors = appColors(baseline, overrides, behavior, resetDone);
            const screen = stageScreen(spec, index, colors, overrides);
            const queries = stageQueries(spec, step, colors, baseline, behavior);
            const view = { theme: stageTheme(spec, step, colors, overrides, baseline, behavior), extra: { caseId: contract.caseId, experimentId: spec.id, stageId: step.id, queries },
                drawn: behavior.wrongRender && step.kind === 'reset' ? { ...stageTheme(spec, step, colors, overrides, baseline, behavior), backgroundArgb: 0xff123456 } : undefined,
                repeat: behavior.stalePng && step.kind === 'reset' ? frames.at(-1) : (behavior.baselinePng && step.kind === 'reset' ? frames[firstFrame] : null) };
            frames.push(frame(frames.length, 'crst-read', screen, deviceAttribute, view, store));
            reads.push({ kind: 'state', cursor: [screen.cursorRow, screen.cursorCol], queries: structuredClone(queries) });
        }
    }
    return reads;
}

/** A complete, valid synthetic run: frames, worker run log, adapter facts and the retained PNGs. `behavior` builds negative evidence. */
export function syntheticColorResetEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), behavior = {}) {
    const baseline = syntheticBaseline(), pngs = new Map(), images = new Map(), store = { pngs, images };
    const host = hostScreen(), hostTheme = renderTheme(baseline, { palette: new Map(), foreground: null, background: null, cursor: null });
    const nonce = 'NTILDE_CR_' + runId;
    const frames = [frame(0, 'crst-start', host, null, { theme: hostTheme }, store)];
    frames.push(frame(1, 'crst-bind', { ...host, lines: [nonce, ...host.lines.slice(1)], cursorCol: nonce.length }, deviceAttribute, { theme: hostTheme }, store));
    const cases = colorResetContracts.map(contract => {
        const reads = caseFrames(contract, frames, store, baseline, behavior);
        const feeds = planFeeds(contract);
        const pinned = { probeId: contract.caseId, ...replayColorResetPinned(contract, contract.pinnedRoles.map(role => reads[contract.roles.indexOf(role)].queries[0].reply)) };
        return { caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute), reads, pinned };
    });
    frames.push(frame(frames.length, 'crst-finish', host, deviceAttribute, { theme: hostTheme }, store));
    const run = { schemaVersion: 1, adapterId: colorResetAdapterId, probeIds: colorResetContracts.map(item => item.caseId), cases };
    const adapter = { id: colorResetAdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, pngs, images, captureOf: item => ({ details: item.pixel.renderDetails, image: images.get(item.pixel.file) }) };
}

/** Write the retained PNGs of one synthetic run under `output/crst-pixels`, as the real observer does. */
export function writeColorResetPngs(evidence, output) {
    mkdirSync(join(output, 'crst-pixels'), { recursive: true });
    for (const [file, bytes] of evidence.pngs) writeFileSync(join(output, file), bytes);
}

/** The independent contract verdict of every case of a synthetic run. */
export function syntheticColorResetGrades(evidence) {
    let at = 2;
    return colorResetContracts.map(contract => {
        const reads = evidence.run.cases.find(item => item.caseId === contract.caseId).reads.map(() => evidence.frames[at++]);
        return gradeColorResetCase(contract, colorResetObservation(contract, reads.map(item => ({ screen: item.screen, queries: item.queries,
            details: item.pixel.renderDetails, pixelSha256: item.pixel.sha256, render: syntheticRenderSamples(evidence, item) }))));
    });
}

export { defaultChannel } from './sgr-palette-evidence.mjs';

/** A deep copy of the mutable parts of a run (journal, run log, adapter), sharing the retained PNG bytes. */
export const cloneColorResetEvidence = evidence => ({ ...evidence, frames: structuredClone(evidence.frames), run: structuredClone(evidence.run), adapter: structuredClone(evidence.adapter) });

/** The colors the synthetic render of one frame drew, read the way the report builder reads a real one. */
export const syntheticRenderSamples = (evidence, frame) => renderSamples({ image: evidence.images.get(frame.pixel.file), details: frame.pixel.renderDetails });
