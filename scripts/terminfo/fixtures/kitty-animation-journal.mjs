// Synthetic protocols/state/PNGs only. These are never runtime evidence.
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { initialInputScreen, inputPaneId, inputRunId, inputCapture } from './input-journal.mjs';
import { pixelCapture } from './pixel-capture.mjs';
import { imageOwnership, rgbaPng } from './static-images-journal.mjs';
import { animationCasesByVersion, animationExpectedReplies, currentAnimationVersion } from '../kitty-animation-cases.mjs';
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
    // A relaunch quits the App and starts it again before the stage is admitted: it begins at the stage's relaunchAtMs into the running timeline when it has one, and the
    // App is back about 2.5 seconds later; otherwise right after the previous read.
    const begin = stage?.relaunch && stage.relaunchAtMs !== undefined && startedAt !== undefined ? startedAt + stage.relaunchAtMs : undefined;
    const actionAt = begin === undefined ? clock + (stage?.waitMs ?? 100) : begin + 2500;
    // A relaunch read follows right after the App is back (the measured captures began well under a second after it), so a later hold stays within the same cycle.
    if (stage?.relaunch && begin === undefined) return { actionAt, readAt: actionAt + 50, span: [actionAt + 100, actionAt + 500] };
    return { actionAt, readAt: actionAt + 600, span: [actionAt + 800, actionAt + 2000] };
}

/** Only the restore suite (version 3) runs in a persistent tab; versions 1 and 2 stay on the direct-worker path. */
const restoreVersion = 3;

/** The ownership proof at a moment: a direct run names the App; a persistent one also names the daemon the worker hangs from and the last relaunch. */
function ownershipStamp(base, { appPid, daemonRecord, relaunchInfo, version }, at) {
    const persistent = version === restoreVersion;
    return { ...structuredClone(base), app: { ...structuredClone(base.app), pid: appPid },
        ...(persistent ? { worker: { ...structuredClone(base.worker), ppid: daemonRecord.pid }, daemon: structuredClone(daemonRecord) } : {}),
        ...(relaunchInfo ? { relaunch: structuredClone(relaunchInfo) } : {}), at: iso(at) };
}

/** The bytes the terminal answered with for one action (the stage's expected replies, optionally rewritten by the reply hook) and the background query's answer. */
function replySample(entry, stage, state, action, reply) {
    const expected = stage ? animationExpectedReplies(stage, state.imageId).join('') : '';
    const raw = reply?.(entry, expected) ?? expected;
    const background = imageBackgroundReply(entry.kind === 'finish' ? 0xff000000 : 0xff101010);
    return inputCapture(Buffer.from(raw + background).toString('hex') + imageModeReplyHex, 100 + action.index);
}

/** What the pane shows at a read: the stage's appearance (a timeline stage depends on the playback start), optionally repainted by the paint hook; non-reads show nothing. */
function appearanceOf(entry, stage, paint, startedAt, span) {
    if (entry.kind !== 'read') return 'absent';
    const shown = stage?.appearance === 'timeline' ? timelineFrame(startedAt, span) : stage?.appearance;
    return paint?.(entry, shown ?? 'absent') ?? shown ?? 'absent';
}

/** The two captures of a pair: the second may show another appearance (pairDiffer); each is written under its frame index. */
function pairPixels(output, frameIndex, screen, appearance, { shownOther, bleed }) {
    return [appearance, shownOther ?? appearance].map((shown, side) => {
        const capture = syntheticAnimationCapture(screen, shown, { bleed: bleed(side) });
        const bytes = Buffer.from(capture.pngBase64, 'base64'), { pngBase64, ...facts } = capture;
        const file = `${animationPixelPrefix}/frame-${String(frameIndex).padStart(3, '0')}-${side}.png`;
        writeFileSync(join(output, file), bytes); return { ...structuredClone(facts), file, sha256: pngHash(bytes) };
    });
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
    // A restore run (the restore version) lives in a persistent tab: the worker is the multiplexer daemon's child, and every relaunch stage brings a new App process (and pane).
    const firstApp = baseOwnership.app.pid, daemonRecord = { pid: 700, ppid: firstApp, uid: baseOwnership.app.uid, tty: '??', executable: baseOwnership.app.executable, ...daemon };
    let appPid = baseOwnership.app.pid, currentPane = paneId, relaunchInfo;
    const stamp = at => ownershipStamp(baseOwnership, { appPid, daemonRecord, relaunchInfo, version }, at);
    const add = (entry, screen, sample, action, appearance, readAt, span) => {
        const pixels = pairPixels(output, frames.length, screen, appearance, { shownOther: pairDiffer?.(entry), bleed: side => bleed?.(entry, side) === true });
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
        let plan = plannedTiming(stage, startedAt.get(entry.caseId), clock);
        plan = timing?.(entry, plan, { startedAt: startedAt.get(entry.caseId) }) ?? plan;
        if (stage?.relaunch && !keepApp) {
            const from = appPid;
            appPid += 1; daemonRecord.ppid = 1; currentPane = relaunchAt?.paneId ?? `${paneId.slice(0, -2)}${String(appPid).slice(-2)}`;
            relaunchInfo = { from, to: appPid, startedAt: iso(Math.max(clock, plan.actionAt - 2200)), at: iso(plan.actionAt - 50) };
        }
        if (stage?.reply === 'start') startedAt.set(entry.caseId, plan.actionAt);
        const action = { index: actions.length, kind: 'kan-' + entry.kind, caseId: entry.caseId ?? null, stageId: entry.stageId ?? null,
            imageId: state.imageId ?? null, commandHex: Buffer.from(expected.command).toString('hex'),
            commandSha256: pngHash(Buffer.from(expected.command)), skipReason: expected.skipReason, ownership: stamp(plan.actionAt) };
        actions.push(action);
        const sample = replySample(entry, stage, state, action, reply);
        const appearance = appearanceOf(entry, stage, paint, startedAt.get(entry.caseId), plan.span);
        add(entry, entry.kind === 'finish' ? initial : bound, sample, action, appearance, plan.readAt, plan.span);
        clock = plan.span[1];
    }
    const final = frames.at(-1).capture;
    const seal = { failed: false, disposed: true, attempts: animationActionCount, ownership: stamp(clock + 50), lastAttempt: { completed: true, fenceRequested: true,
        hex: final.hex + Buffer.from(final.barrier).toString('hex'), nonces: final.nonces, error: null } };
    return { frames, actions, seal, runId };
}
