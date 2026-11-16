import { readFileSync, lstatSync } from 'node:fs';
import { join } from 'node:path';
import { snapshotHash, screenSnapshot } from './agent-observer.mjs';
import { inputPriorCaptureCap } from './input-plan.mjs';
import { animationCases, animationCommand, animationExpectedReplies, animationSuiteId, animationAnchor, animationCellEnvelope, animationHoldMs } from './kitty-animation-cases.mjs';
import { animationFrameInput, animationFrameIds } from './kitty-animation-inputs.mjs';
import { inspectAnimationPixels, evaluateAnimationTiming } from './kitty-animation-oracle.mjs';
import { checkImages, imagePlan, imageSetup, imageRestore, imagePosition, imageBoundScreen, pristineImageBaseline, imageRawReplies, stableImageContext,
    validateImageCapture, validateImageOwnership } from './static-images.mjs';
import { pngHash, totalPixelLimit } from './pixels-capture.mjs';

export const ownedAnimationPolicy = 'owned-kitty-animation-paired-raw-render-v1';
export const animationPlan = Object.freeze([
    { kind: 'bind' }, ...animationCases.flatMap(item => [...item.stages.map(stage => ({ kind: 'read', caseId: item.id, stageId: stage.id })),
        { kind: 'restore', caseId: item.id }]), { kind: 'finish' },
].map(Object.freeze));
export const animationActionCount = animationPlan.length;
/** The input phase's challenge pairs plus the static-image suite's (one per capture) that this suite must also avoid. */
export const animationPriorCap = inputPriorCaptureCap + imagePlan.length + 1;
export const animationPlannedStages = animationCases.reduce((total, item) => total + item.stages.length, 0);
export const animationPixelPrefix = 'animation-pixels';
const framePixelFile = (index, side) => `${animationPixelPrefix}/frame-${String(index).padStart(3, '0')}-${side}.png`;
const uint = value => Number.isSafeInteger(value) && value >= 1 && value <= 0xffffffff;

/** Parent chooses each case's image id; replies must echo it exactly. */
export function animationAction(entry, state, runId, initialCursorVisible) {
    if (entry.kind === 'bind' || entry.kind === 'restore') return { command: imageSetup(runId), skipReason: null };
    if (entry.kind === 'finish') return { command: imageRestore(initialCursorVisible), skipReason: null };
    checkImages(uint(state.imageId));
    return { command: imagePosition(runId) + animationCommand(entry.caseId, entry.stageId, state) + imagePosition(runId), skipReason: null };
}

/** Exact framing of consecutive APC replies; mixed or uncorrelated bytes invalidate the experiment. */
export function parseAnimationReplies(raw, imageId) {
    checkImages(typeof raw === 'string' && raw.length <= 1024 && uint(imageId));
    const prefix = '\x1b_Gi=', terminator = '\x1b\\', replies = []; let rest = raw;
    while (rest.length) {
        const end = rest.indexOf(terminator), body = end > 0 && rest.startsWith(prefix) ? rest.slice(prefix.length, end) : '', separator = body.indexOf(';');
        const fields = body.slice(0, separator).split(','), status = body.slice(separator + 1);
        checkImages(separator > 0 && fields.length <= 2 && /^\d{1,10}$/.test(fields[0]) && Number(fields[0]) === imageId
            && (fields.length === 1 || /^p=[1-9]\d{0,9}$/.test(fields[1])) && /^(OK|E[A-Z\d]+(?::[ -~]*)?)$/.test(status));
        replies.push({ status, placement: fields.length === 2 ? Number(fields[1].slice(2)) : null }); rest = rest.slice(end + terminator.length);
    }
    return replies;
}
/** Missing or error replies are semantic negatives; extra, reordered-id or malformed bytes are invalid. */
export function animationReply(raw, stage, imageId) {
    const expected = parseAnimationReplies(animationExpectedReplies(stage.id, imageId).join(''), imageId), parsed = parseAnimationReplies(raw, imageId);
    checkImages(parsed.length <= expected.length && parsed.every((reply, index) => reply.placement === expected[index].placement));
    const statuses = parsed.map(reply => reply.status);
    if (!expected.length) return { passed: true, statuses };
    return { passed: statuses.length === expected.length && statuses.every(status => status === 'OK'), statuses };
}

export function animationBoundScreen(initial, runId) { return imageBoundScreen(initial, runId); }
export function animationInputs() {
    return animationFrameIds.map(frameId => {
        const input = animationFrameInput(frameId);
        return { file: `animation-inputs/${frameId}.rgba`, bytes: input.bytes, sha256: input.sha256 };
    });
}
/** The shared image-capture validator keys its original-theme rule on the carrier's start/finish kinds. */
export const imageCaptureKind = kind => kind.replace(/^kan-/, 'images-');
export function retainedAnimationPixel(output, pixel, screen, kind) {
    checkImages(/^animation-pixels\/frame-\d{3}-[01]\.png$/.test(pixel?.file ?? ''));
    const path = join(output, pixel.file), stat = lstatSync(path);
    checkImages(stat.isFile() && !stat.isSymbolicLink() && stat.size <= 16 * 1024 * 1024);
    const bytes = readFileSync(path); checkImages(pngHash(bytes) === pixel.sha256);
    return validateImageCapture({ ...pixel, pngBase64: bytes.toString('base64') }, screen, imageCaptureKind(kind));
}
export const animationSpanOf = frame => {
    const span = frame?.pairSpan;
    checkImages(span && Number.isSafeInteger(span.startMs) && Number.isSafeInteger(span.endMs) && span.startMs <= span.endMs);
    return { start: span.startMs, end: span.endMs };
};

export function buildAnimationReport(frames, actions, seal, runId, output, priorChallenges = []) {
    checkImages(/^[a-f\d]{32}$/.test(runId ?? '') && frames?.length === animationActionCount + 1 && actions?.length === animationActionCount
        && Array.isArray(priorChallenges) && priorChallenges.length <= animationPriorCap && new Set(priorChallenges).size === priorChallenges.length);
    const builder = new AnimationReport(frames, actions, runId, output, priorChallenges);
    frames.forEach((frame, index) => builder.processFrame(frame, index));
    builder.checkSeal(seal);
    return { schemaVersion: 1, suiteId: animationSuiteId, runId, policy: ownedAnimationPolicy,
        scope: 'Three bounded Kitty animation cases; independently captured raw replies, paired original offscreen PNGs, complete cells/modes and wall-clock capture spans. '
            + 'Elapsed-time claims are bounded to this App build and run: no display-refresh cadence, other platform, frame-rate, snapshot/persistent-session or stock-grade claim.',
        holdMs: animationHoldMs, inputs: animationInputs().map(({ bytes, ...value }) => ({ ...value, byteCount: bytes.length })), priorChallenges,
        queriedPrivateModes: [5, 66, 9, 1000, 1002, 1003, 1006], kittyKeyboardFlags: 0, backgroundControls: builder.backgrounds,
        initialSnapshot: 0, restoredSnapshot: animationActionCount, snapshots: frames, actions, seal,
        caseResults: builder.caseResults(), observations: builder.observations };
}

class AnimationReport {
    constructor(frames, actions, runId, output, priorChallenges) {
        this.frames = frames; this.actions = actions; this.runId = runId; this.output = output;
        this.initial = frames[0].screen; this.paneId = frames[0].paneId; this.bound = animationBoundScreen(this.initial, runId);
        checkImages(pristineImageBaseline(this.initial) && this.initial.rows >= 8 && this.initial.cols >= 60);
        this.used = new Set(priorChallenges); this.states = new Map(); this.observations = []; this.backgrounds = []; this.total = 0;
        this.lastTime = -Infinity; this.lastEnd = -Infinity; this.facts = new Map();
    }
    validateFrame(frame, index, entry) {
        validateImageOwnership(frame.ownership, this.frames[0].ownership, this.initial);
        checkImages(frame.index === index && frame.kind === 'kan-' + entry.kind && frame.paneId === this.paneId
            && frame.caseId === (entry.caseId ?? null) && frame.stageId === (entry.stageId ?? null));
        for (const screen of [frame.screen, frame.afterScreen]) screenSnapshot(screen, false, true, true, true, true);
        const expectedScreen = ['start', 'finish'].includes(entry.kind) ? this.initial : this.bound;
        checkImages(frame.sha256 === snapshotHash(frame.screen) && frame.afterSha256 === snapshotHash(frame.afterScreen)
            && snapshotHash(frame.screen) === snapshotHash(expectedScreen) && snapshotHash(frame.afterScreen) === snapshotHash(expectedScreen));
    }
    readPixels(frame, index) {
        checkImages(frame.pixels?.length === 2);
        const pixels = frame.pixels.map((pixel, side) => {
            checkImages(pixel.file === framePixelFile(index, side));
            const value = retainedAnimationPixel(this.output, pixel, frame.screen, frame.kind); this.total += value.image.width * value.image.height;
            const stable = stableImageContext(value.details); this.context ??= stable; checkImages(stable === this.context);
            return value;
        });
        checkImages(this.total <= totalPixelLimit && frame.pixels[0].sha256 === frame.pixels[1].sha256
            && snapshotHash(frame.pixels[0].renderDetails) === snapshotHash(frame.pixels[1].renderDetails)
            && snapshotHash(frame.pixels[0].imageDetails) === snapshotHash(frame.pixels[1].imageDetails));
        return pixels;
    }
    validateAction(frame, index, entry) {
        const action = this.actions[index - 1];
        validateImageOwnership(action.ownership, this.frames[0].ownership, this.initial);
        const actionTime = Date.parse(action.ownership.at), frameTime = Date.parse(frame.ownership.at);
        checkImages(this.lastTime <= actionTime && actionTime <= frameTime); this.lastTime = frameTime;
        checkImages(action.index === index - 1 && frame.actionIndex === action.index && action.kind === frame.kind
            && action.caseId === frame.caseId && action.stageId === frame.stageId);
        if (entry.kind === 'read' && !this.states.has(entry.caseId)) {
            checkImages(uint(action.imageId) && ![...this.states.values()].some(value => value.imageId === action.imageId));
            this.states.set(entry.caseId, { imageId: action.imageId });
        }
        const state = this.states.get(entry.caseId) ?? {}, expected = animationAction(entry, state, this.runId, this.initial.cursorVisible);
        checkImages(action.commandHex === Buffer.from(expected.command).toString('hex') && action.commandSha256 === pngHash(Buffer.from(expected.command))
            && action.skipReason === expected.skipReason && action.imageId === (state.imageId ?? null));
        return { action, state, expected, actionTime, frameTime };
    }
    readReply(frame) {
        const argb = frame.kind === 'kan-finish' ? this.initialBackground : 0xff101010;
        const { replyBytes, backgroundQuery } = imageRawReplies(frame.capture, this.used, argb);
        this.backgrounds.push({ snapshot: frame.index, argb, query: backgroundQuery });
        return replyBytes;
    }
    checkControl(entry, frame, pixels, replyBytes) {
        checkImages(replyBytes.length === 0 && pixels.every(value => value.imageDetails.totalPlacements === 0));
        if (entry.kind === 'bind') this.boundPixel = pixels[0];
        if (entry.kind === 'restore') checkImages(frame.pixels[0].sha256 === pngHash(this.boundPixel.bytes));
        if (entry.kind === 'finish') checkImages(frame.pixels[0].sha256 === this.initialPixel
            && snapshotHash(frame.pixels[0].renderDetails) === snapshotHash(this.frames[0].pixels[0].renderDetails));
    }
    /** Wall-clock facts: when a command could have taken effect, and the span the paired captures covered. */
    timingFacts(entry, stage, index, { action, actionTime, frameTime }) {
        const span = animationSpanOf(this.frames[index]);
        checkImages(frameTime <= span.start && this.lastEnd <= actionTime);
        if (stage.waitMs !== undefined) checkImages(actionTime - this.lastEnd >= stage.waitMs);
        this.lastEnd = span.end;
        const caseFacts = this.facts.get(entry.caseId) ?? {};
        if (stage.reply === 'build') caseFacts.build = { earliest: actionTime, latest: frameTime };
        if (stage.reply === 'start') caseFacts.start = { earliest: actionTime, latest: frameTime };
        if (stage.reply === 'stop') caseFacts.stop = { earliest: actionTime, latest: frameTime };
        this.facts.set(entry.caseId, caseFacts);
        return { facts: { ...caseFacts, capture: span }, action };
    }
    observe(entry, index, pixels, replyBytes, context) {
        const item = animationCases.find(value => value.id === entry.caseId), stage = item.stages.find(value => value.id === entry.stageId);
        const reply = animationReply(replyBytes.toString('ascii'), stage, context.state.imageId);
        const { facts } = this.timingFacts(entry, stage, index, context);
        const timing = evaluateAnimationTiming(stage, facts);
        const pixel = inspectAnimationPixels({ baseline: this.boundPixel.image, observed: pixels[0].image, grid: pixels[0].details.grid,
            anchor: animationAnchor, cells: animationCellEnvelope, appearance: stage.appearance });
        checkImages(pixel.controls[0].passed);
        const assertions = [...pixel.assertions, pixel.controls[1]];
        const grade = this.gradeStage(item, stage, reply, assertions, timing);
        this.observations.push({ featureId: item.id + '.' + stage.id, ...grade, snapshot: index, actionIndex: context.action.index,
            reply, assertions, classification: pixel.classification, measurement: pixel.measurement,
            timing: { clock: stage.clock, valid: timing.valid, reason: timing.reason, facts } });
    }
    gradeStage(item, stage, reply, assertions, timing) {
        const position = item.stages.findIndex(value => value.id === stage.id);
        const witnessIds = position === 0 ? [] : [...new Set(['blank', item.stages[position - 1].id])];
        const causality = witnessIds.map(id => {
            const featureId = item.id + '.' + id, witness = this.observations.find(value => value.featureId === featureId);
            checkImages(witness);
            return { featureId, snapshot: witness.snapshot, result: witness.result, passed: witness.result === 'passed' };
        });
        const missing = causality.filter(value => !value.passed);
        let inconclusiveReason = null;
        if (missing.length) inconclusiveReason = 'requires passed prior witness: ' + missing.map(value => value.featureId + ' (' + value.result + ')').join(', ');
        if (!inconclusiveReason && !timing.valid) inconclusiveReason = timing.reason;
        // A negative reply or bleed outside the target stands without timing; a pixel mismatch needs valid timing and witnesses.
        const independentNegative = reply.passed === false || assertions.at(-1).passed === false;
        let result = 'inconclusive';
        if (independentNegative) { result = 'failed'; inconclusiveReason = null; }
        else if (!inconclusiveReason) result = assertions.every(value => value.passed) ? 'passed' : 'failed';
        return { result, causality, inconclusiveReason };
    }
    caseResults() {
        return animationCases.map(item => {
            const stages = this.observations.filter(value => value.featureId.startsWith(item.id + '.'));
            checkImages(stages.length === item.stages.length);
            let result = 'passed';
            if (stages.some(value => value.result === 'inconclusive')) result = 'inconclusive';
            if (stages.some(value => value.result === 'failed')) result = 'failed';
            return { caseId: item.id, result, stages: stages.map(value => value.featureId) };
        });
    }
    processFrame(frame, index) {
        const entry = index ? animationPlan[index - 1] : { kind: 'start' };
        this.validateFrame(frame, index, entry); const pixels = this.readPixels(frame, index);
        if (!index) {
            checkImages(frame.capture === null && frame.actionIndex === null); this.initialPixel = frame.pixels[0].sha256;
            this.initialBackground = pixels[0].details.theme.backgroundArgb;
            checkImages(pixels[0].imageDetails.totalPlacements === 0);
            this.lastTime = Date.parse(frame.ownership.at); this.lastEnd = animationSpanOf(frame).end; return;
        }
        const facts = this.validateAction(frame, index, entry), replyBytes = this.readReply(frame);
        if (entry.kind !== 'read') this.checkControl(entry, frame, pixels, replyBytes);
        else this.observe(entry, index, pixels, replyBytes, facts);
    }
    checkSeal(seal) {
        const last = this.frames.at(-1).capture;
        validateImageOwnership(seal?.ownership, this.frames[0].ownership, this.initial);
        checkImages(this.lastTime <= Date.parse(seal.ownership.at));
        checkImages(seal?.failed === false && seal.disposed === true && seal.attempts === animationActionCount && seal.lastAttempt?.completed === true
            && seal.lastAttempt.error === null && seal.lastAttempt.fenceRequested === true
            && seal.lastAttempt.hex === last.hex + Buffer.from(last.barrier).toString('hex')
            && snapshotHash(seal.lastAttempt.nonces) === snapshotHash(last.nonces));
        for (const input of animationInputs()) {
            const stat = lstatSync(join(this.output, input.file)); checkImages(stat.isFile() && !stat.isSymbolicLink() && stat.size === input.bytes.length);
            checkImages(pngHash(readFileSync(join(this.output, input.file))) === input.sha256);
        }
    }
}
