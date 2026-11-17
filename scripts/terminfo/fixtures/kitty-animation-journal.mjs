// Synthetic protocols/state/PNGs only. These are never runtime evidence.
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { initialInputScreen, inputPaneId, inputRunId, inputCapture } from './input-journal.mjs';
import { pixelCapture } from './pixel-capture.mjs';
import { imageOwnership, rgbaPng } from './static-images-journal.mjs';
import { animationCases, animationCasesByVersion, animationExpectedReplies, currentAnimationVersion } from '../kitty-animation-cases.mjs';
import { animationAppearanceColor } from '../kitty-animation-inputs.mjs';
import { animationSuites, animationAction, animationInputs, animationBoundScreen, animationPixelPrefix } from '../kitty-animation.mjs';
import { imageModeReplyHex, imageBackgroundReply } from '../static-images.mjs';
import { snapshotHash } from '../agent-observer.mjs';
import { pngHash } from '../pixels-capture.mjs';

export const animationEpoch = Date.parse('2026-11-04T14:00:00.000Z');
const iso = ms => new Date(animationEpoch + ms).toISOString();
/** The frame the timeline gives at the middle of a capture span (tests keep the span well inside one window). */
const timelineFrame = (startedAt, span) => ['root', 'second', 'third'][Math.floor(Math.max(0, (span[0] + span[1]) / 2 - startedAt) / 5000) % 3];

/** A 32x32 aspect-fit square of one frame color at the controlled anchor, or blank; optional bleed outside it. */
export function syntheticAnimationCapture(screen, appearance, { bleed = false } = {}) {
    const capture = pixelCapture(screen), width = screen.cols * 8, height = screen.rows * 18;
    const rgba = Buffer.alloc(width * height * 4), background = screen.bufferState.alternateScreen ? 16 : 0;
    for (let i = 0; i < rgba.length; i += 4) rgba.set([background, background, background, 255], i);
    if (appearance && appearance !== 'absent') {
        const color = animationAppearanceColor(appearance).color;
        for (let y = 0; y < 32; y++) for (let x = 0; x < 32; x++) rgba.set(color, ((72 + y) * width + 24 + x) * 4);
    }
    if (bleed) rgba.set([255, 0, 0, 255], 0);
    const bytes = rgbaPng(width, height, rgba);
    Object.assign(capture, { width, height, byteCount: bytes.length, pngBase64: bytes.toString('base64') });
    Object.assign(capture.renderDetails.theme, { backgroundOverride: screen.bufferState.alternateScreen,
        backgroundArgb: screen.bufferState.alternateScreen ? 0xff101010 : 0xff000000,
        cursorTextArgb: screen.bufferState.alternateScreen ? 0xff101010 : 0xff000000,
        cursorOverride: false, selectionBackgroundOverride: false, selectionForegroundOverride: false,
        selectionBackgroundArgb: null, selectionForegroundArgb: null, effectiveSelectionBackgroundArgb: 0xff123456,
        effectiveSelectionForegroundArgb: null, paletteOverrideIndices: [] });
    Object.assign(capture.renderDetails.grid, { renderWidthPx: width, renderHeightPx: height, cellWidthDip: 8, cellHeightDip: 18,
        cellWidthPx: 8, cellHeightPx: 18, baselineDip: 14, baselineOffsetPx: 14, underlineOffsetPx: 16, strikeOffsetPx: 9 });
    capture.imageDetails = appearance && appearance !== 'absent'
        ? { schemaVersion: 1, complete: true, totalPlacements: 1, placements: [{ index: 0, placementId: '11111111-2222-3333-4444-555555555555', col: 3,
            viewportRow: 4, widthCells: 4, heightCells: 2, clipTopRows: 0, clipBottomRows: 0, clipRightColumns: 0, preserveAspectRatio: true,
            bitmapWidthPx: 16, bitmapHeightPx: 16, sixelPlacement: null }] }
        : { schemaVersion: 1, complete: true, totalPlacements: 0, placements: [] };
    return capture;
}

/** Where a stage's admission, read request and capture span fall on the simulated clock: a running read centres on its target, any other waits out its gap. */
function plannedTiming(stage, startedAt, clock) {
    if (stage?.clock === 'running' && stage.targetMs !== undefined && startedAt !== undefined) {
        const centre = startedAt + stage.targetMs, span = [centre - 600, centre + 600];
        return { actionAt: span[0] - 1000, readAt: span[0] - 200, span };
    }
    const actionAt = clock + (stage?.waitMs ?? 100);
    return { actionAt, readAt: actionAt + 600, span: [actionAt + 800, actionAt + 2000] };
}

/**
 * A scripted run on a simulated wall clock. Running reads aim their capture centre at the stage's target; holds wait out their gap.
 * Hooks: paint(entry, appearance) -> appearance, reply(entry, raw) -> raw, timing(entry, plan) -> plan, bleed(entry, side) -> boolean, pairDiffer(entry) -> the appearance of the second capture of a pair.
 */
export function syntheticAnimationJournal(output, { runId = inputRunId, paneId = inputPaneId, initial = initialInputScreen(10, 60), paint, reply, timing, bleed, pairDiffer, version = currentAnimationVersion, relaunchAt, daemon, keepApp } = {}) {
    const bound = animationBoundScreen(initial, runId), frames = [], actions = [], states = new Map();
    const { plan: animationPlan } = animationSuites[version], cases = animationCasesByVersion[version], animationActionCount = animationPlan.length;
    mkdirSync(join(output, animationPixelPrefix)); mkdirSync(join(output, 'animation-inputs'));
    for (const input of animationInputs(version)) writeFileSync(join(output, input.file), input.bytes);
    const baseOwnership = imageOwnership(initial.rows, initial.cols, runId);
    // A restore run (version 3) lives in a persistent tab: the worker is the multiplexer daemon's child, and every relaunch stage brings a new App process (and pane).
    const firstApp = baseOwnership.app.pid, daemonRecord = { pid: 700, ppid: firstApp, uid: baseOwnership.app.uid, tty: '??', executable: baseOwnership.app.executable, ...daemon };
    let appPid = baseOwnership.app.pid, currentPane = paneId;
    const stamp = at => ({ ...structuredClone(baseOwnership), app: { ...structuredClone(baseOwnership.app), pid: appPid }, ...(version === currentAnimationVersion ? {} : { worker: { ...structuredClone(baseOwnership.worker), ppid: daemonRecord.pid }, daemon: structuredClone(daemonRecord) }), at: iso(at) });
    const add = (entry, screen, sample, action, appearance, readAt, span) => {
        const other = pairDiffer?.(entry);
        const pixels = [appearance, other ?? appearance].map((shown, side) => {
            const capture = syntheticAnimationCapture(screen, shown, { bleed: bleed?.(entry, side) === true });
            const bytes = Buffer.from(capture.pngBase64, 'base64'), { pngBase64, ...facts } = capture;
            const file = `${animationPixelPrefix}/frame-${String(frames.length).padStart(3, '0')}-${side}.png`;
            writeFileSync(join(output, file), bytes); return { ...structuredClone(facts), file, sha256: pngHash(bytes) };
        });
        frames.push({ index: frames.length, kind: 'kan-' + entry.kind, caseId: entry.caseId ?? null, stageId: entry.stageId ?? null,
            paneId: currentPane, screen: structuredClone(screen), afterScreen: structuredClone(screen), sha256: snapshotHash(screen),
            afterSha256: snapshotHash(screen), pixels, pairSpan: { startMs: animationEpoch + span[0], endMs: animationEpoch + span[1] },
            ownership: stamp(readAt), actionIndex: action?.index ?? null, capture: sample });
    };
    add({ kind: 'start' }, initial, null, null, 'absent', 0, [100, 900]);
    let clock = 1000; const startedAt = new Map();
    for (const entry of animationPlan) {
        if (entry.kind === 'read' && !states.has(entry.caseId)) states.set(entry.caseId, { imageId: 4000 + states.size });
        const state = states.get(entry.caseId) ?? {}, expected = animationAction(entry, state, runId, initial.cursorVisible, version);
        const item = cases.find(value => value.id === entry.caseId), stage = item?.stages.find(value => value.id === entry.stageId);
        if (stage?.relaunch && !keepApp) { appPid += 1; daemonRecord.ppid = 1; currentPane = relaunchAt?.paneId ?? `${paneId.slice(0, -2)}${String(appPid).slice(-2)}`; }
        let plan = plannedTiming(stage, startedAt.get(entry.caseId), clock);
        plan = timing?.(entry, plan, { startedAt: startedAt.get(entry.caseId) }) ?? plan;
        if (stage?.reply === 'start') startedAt.set(entry.caseId, plan.actionAt);
        const action = { index: actions.length, kind: 'kan-' + entry.kind, caseId: entry.caseId ?? null, stageId: entry.stageId ?? null,
            imageId: state.imageId ?? null, commandHex: Buffer.from(expected.command).toString('hex'),
            commandSha256: pngHash(Buffer.from(expected.command)), skipReason: expected.skipReason, ownership: stamp(plan.actionAt) };
        actions.push(action);
        let raw = stage ? animationExpectedReplies(stage, state.imageId).join('') : '';
        raw = reply?.(entry, raw) ?? raw;
        const background = imageBackgroundReply(entry.kind === 'finish' ? 0xff000000 : 0xff101010);
        const sample = inputCapture(Buffer.from(raw + background).toString('hex') + imageModeReplyHex, 100 + action.index);
        const shown = stage?.appearance === 'timeline' ? timelineFrame(startedAt.get(entry.caseId), plan.span) : stage?.appearance;
        const appearance = paint?.(entry, shown ?? 'absent') ?? shown ?? 'absent';
        add(entry, entry.kind === 'finish' ? initial : bound, sample, action, entry.kind === 'read' ? appearance : 'absent', plan.readAt, plan.span);
        clock = plan.span[1];
    }
    const final = frames.at(-1).capture;
    const seal = { failed: false, disposed: true, attempts: animationActionCount, ownership: stamp(clock + 50), lastAttempt: { completed: true, fenceRequested: true,
        hex: final.hex + Buffer.from(final.barrier).toString('hex'), nonces: final.nonces, error: null } };
    return { frames, actions, seal, runId };
}
