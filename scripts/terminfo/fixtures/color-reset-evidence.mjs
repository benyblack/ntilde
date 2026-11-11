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
import { colorResetContracts, colorResetFixture, colorResetObservation, gradeColorResetCase } from '../color-reset-contracts.mjs';
import { colorResetModel } from '../color-reset-contracts.mjs';
import { opaqueArgb, channels16, overrideIndices } from '../color-reset-model.mjs';
import { replayColorResetPinned } from '../color-reset-grading.mjs';
import { colorResetAdapterId } from '../color-reset-grading-collector.mjs';
import { syntheticColEditScreen, colEditRows, colEditCols, paletteBinding, colorCell, plainCell } from './col-edit-evidence.mjs';
import { paletteChannel, literalChannel, defaultChannel } from './sgr-palette-evidence.mjs';
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
    if (step.kind === 'reset' && behavior.wrongTarget && spec.id === 'isolation') { clone.background = null; clone.cursor = null; clone.foreground = null; }
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

/** A PNG whose bytes depend on exactly what the render shows: the screen and the theme. */
function renderPng(screen, theme) {
    const key = createHash('sha256').update(snapshotHash(screen) + snapshotHash(theme)).digest();
    const pixels = Buffer.alloc(width * height * 4, 0);
    for (let at = 0; at < key.length; at++) pixels[at * 4] = key[at];
    return encodePng({ width, height, pixels });
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
const frame = (index, kind, screen, barrier, theme, extra, pngs, repeat = null) => {
    const png = repeat === null ? renderPng(screen, theme) : pngs.get(repeat.pixel.file), pixel = pixelOf(index, screen, theme, png);
    pngs.set(pixel.file, png);
    return { index, kind, paneId, barrier, screen, sha256: snapshotHash(screen), afterScreen: structuredClone(screen), afterSha256: snapshotHash(screen),
        caseId: null, experimentId: null, stageId: null, queries: [], ...extra, pixel };
};

/** The pristine host screen every run starts and finishes on. */
function hostScreen(colors = syntheticBaseline()) {
    const binding = paletteBinding({ palette: colors.palette, foreground: colors.foreground, background: colors.background });
    return screenSnapshot({ ...syntheticColEditScreen([], [0, 0], undefined, [], binding), cursorVisible: true, cursorMode: hostMode }, true, true, true, true, false, true);
}

/** A complete, valid synthetic run: frames, worker run log, adapter facts and the retained PNGs. `behavior` builds negative evidence. */
export function syntheticColorResetEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), behavior = {}) {
    const baseline = syntheticBaseline(), pngs = new Map();
    const host = hostScreen(), hostTheme = renderTheme(baseline, { palette: new Map(), foreground: null, background: null, cursor: null });
    const nonce = 'NTILDE_CR_' + runId;
    const frames = [frame(0, 'crst-start', host, null, hostTheme, {}, pngs)];
    frames.push(frame(1, 'crst-bind', { ...host, lines: [nonce, ...host.lines.slice(1)], cursorCol: nonce.length }, deviceAttribute, hostTheme, {}, pngs));
    const cases = colorResetContracts.map(contract => {
        const reads = [];
        let at = 0;
        for (const spec of contract.experiments) {
            const resetDone = { value: false };
            for (const [index, step] of spec.stages.entries()) {
                if (step.kind === 'reset') resetDone.value = true;
                const overrides = appOverrides(spec, index, behavior), colors = appColors(baseline, overrides, behavior, resetDone.value);
                const screen = stageScreen(spec, index, colors, overrides);
                const staleView = behavior.renderStale && step.kind === 'reset' ? appOverrides(spec, 1, {}) : overrides;
                const theme = renderTheme(behavior.renderStale && step.kind === 'reset' ? appColors(baseline, staleView, {}, false) : colors, staleView);
                const modelColors = behavior.staleReplies && step.kind === 'reset' ? appColors(baseline, appOverrides(spec, 1, {}), {}, false) : colors;
                const silent = behavior.noReplies || (behavior.noResetReplies && step.kind === 'reset');
                const queries = silent ? spec.queries.map(item => ({ spec: item, barrier: deviceAttribute, reply: null }))
                    : spec.queries.map(item => ({ spec: item, barrier: deviceAttribute, reply: reply(item, queryColor(modelColors, item)) }));
                const extra = { caseId: contract.caseId, experimentId: spec.id, stageId: step.id, queries };
                frames.push(frame(frames.length, 'crst-read', screen, deviceAttribute, theme, extra, pngs, behavior.stalePng && step.kind === 'reset' ? frames.at(-1) : null));
                reads.push({ kind: 'state', cursor: [screen.cursorRow, screen.cursorCol], queries: structuredClone(queries) });
                at++;
            }
        }
        const feeds = planFeeds(contract);
        const pinned = { probeId: contract.caseId, ...replayColorResetPinned(contract, contract.pinnedRoles.map(role => reads[contract.roles.indexOf(role)].queries[0].reply)) };
        return { caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute), reads, pinned };
    });
    frames.push(frame(frames.length, 'crst-finish', host, deviceAttribute, hostTheme, {}, pngs));
    const run = { schemaVersion: 1, adapterId: colorResetAdapterId, probeIds: colorResetContracts.map(item => item.caseId), cases };
    const adapter = { id: colorResetAdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, pngs, captureOf: item => ({ details: item.pixel.renderDetails }) };
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
            details: item.pixel.renderDetails, pixelSha256: item.pixel.sha256 }))));
    });
}

export { defaultChannel };

/** A deep copy of the mutable parts of a run (journal, run log, adapter), sharing the retained PNG bytes. */
export const cloneColorResetEvidence = evidence => ({ ...evidence, frames: structuredClone(evidence.frames), run: structuredClone(evidence.run), adapter: structuredClone(evidence.adapter) });
