import { randomInt } from 'node:crypto';
import { readFileSync, lstatSync } from 'node:fs';
import { join } from 'node:path';
import { inputPriorCaptureCap } from './input-plan.mjs';
import { animationCases, animationCasesByVersion, animationCommand, animationExpectedReplies, animationSuiteIds, currentAnimationVersion, animationAnchor, animationCellEnvelope, animationHoldMs } from './kitty-animation-cases.mjs';
import { animationFrameInput, animationFrameIds, animationPngIds, animationPngInput } from './kitty-animation-inputs.mjs';
import { inspectAnimationPixels, evaluateAnimationTiming, timelineAppearance } from './kitty-animation-oracle.mjs';
import { checkImages, imagePlan, imageSetup, imageRestore, imagePosition, imageBoundScreen, StaticImageReport, validateImageCapture } from './static-images.mjs';
import { pngHash } from './pixels-capture.mjs';

/** Policies by suite version; a retained run's context names the one it was collected under, and its evidence is rederived under that version's plan. */
export const ownedAnimationPolicies = Object.freeze({ 1: 'owned-kitty-animation-paired-raw-render-v1', 2: 'owned-kitty-animation-paired-raw-render-v2', 3: 'owned-kitty-animation-paired-raw-render-restore-v1' });
export const ownedAnimationPolicy = ownedAnimationPolicies[currentAnimationVersion];
/** The suite version a retained policy string names, or null for an unknown one. */
export const animationVersionOfPolicy = policy => Object.keys(ownedAnimationPolicies).map(Number).find(version => ownedAnimationPolicies[version] === policy) ?? null;
const planOf = cases => Object.freeze([
    { kind: 'bind' }, ...cases.flatMap(item => [...item.stages.map(stage => ({ kind: 'read', caseId: item.id, stageId: stage.id })),
        { kind: 'restore', caseId: item.id }]), { kind: 'finish' },
].map(Object.freeze));
export const animationPlan = planOf(animationCases);
export const animationActionCount = animationPlan.length;
/** The input phase's challenge pairs plus the static-image suite's (one per capture) that this suite must also avoid. */
export const animationPriorCap = inputPriorCaptureCap + imagePlan.length + 1;
export const animationPlannedStages = animationCases.reduce((total, item) => total + item.stages.length, 0);
export const animationPixelPrefix = 'animation-pixels';
const framePixelFile = (index, side) => `${animationPixelPrefix}/frame-${String(index).padStart(3, '0')}-${side}.png`;
const uint = value => Number.isSafeInteger(value) && value >= 1 && value <= 0xffffffff;

const mayDiffer = cases => entry => {
    const stage = entry?.kind === 'read' ? cases.find(item => item.id === entry.caseId)?.stages.find(item => item.id === entry.stageId) : null;
    return stage !== undefined && stage !== null && stage.clock !== 'static';
};
/** A paired capture may differ only for a stage whose frame moves or is claimed frozen: a legitimate boundary can fall between the two. */
export const animationPairMayDiffer = mayDiffer(animationCases);

/** Parent chooses each case's image id; replies must echo it exactly. */
export function animationAction(entry, state, runId, initialCursorVisible, version = currentAnimationVersion) {
    if (entry.kind === 'bind' || entry.kind === 'restore') return { command: imageSetup(runId), skipReason: null };
    if (entry.kind === 'finish') return { command: imageRestore(initialCursorVisible), skipReason: null };
    checkImages(uint(state.imageId));
    return { command: imagePosition(runId) + animationCommand(entry.caseId, entry.stageId, state, version) + imagePosition(runId), skipReason: null };
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
    const expected = parseAnimationReplies(animationExpectedReplies(stage, imageId).join(''), imageId), parsed = parseAnimationReplies(raw, imageId);
    checkImages(parsed.length <= expected.length && parsed.every((reply, index) => reply.placement === expected[index].placement));
    const statuses = parsed.map(reply => reply.status);
    if (!expected.length) return { passed: true, statuses };
    return { passed: statuses.length === expected.length && statuses.every(status => status === 'OK'), statuses };
}

export function animationBoundScreen(initial, runId) { return imageBoundScreen(initial, runId); }
/** The retained protocol inputs of a suite version: raw frames for v1, plus the PNG frames from v2. */
export function animationInputs(version = currentAnimationVersion) {
    return [...animationFrameIds.map(frameId => {
        const input = animationFrameInput(frameId);
        return { file: `animation-inputs/${frameId}.rgba`, bytes: input.bytes, sha256: input.sha256 };
    }), ...(version >= 2 ? animationPngIds : []).map(pngId => {
        const input = animationPngInput(pngId);
        return { file: `animation-inputs/${pngId}.png`, bytes: input.bytes, sha256: input.sha256 };
    })];
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

const scopeTail = 'independently captured raw replies, paired original offscreen PNGs, complete cells/modes and wall-clock capture spans. '
    + 'Elapsed-time claims are bounded to this App build and run: no display-refresh cadence, other platform, frame-rate, snapshot/persistent-session or stock-grade claim.';
const scopes = Object.freeze({
    1: 'Three bounded Kitty animation cases; ' + scopeTail,
    2: 'Four bounded Kitty animation cases (a stationary control, playback, stop-freeze, and PNG and root-based frames); ' + scopeTail,
    3: 'Three bounded Kitty animation restore cases in a persistent tab (a running animation, a stopped one, and PNG and root-edited frames), each read again after the App was quit and started again on the same persistent session; '
        + 'independently captured raw replies, paired original offscreen PNGs, complete cells/modes and wall-clock capture spans. Elapsed-time claims are bounded to this App build and run: '
        + 'no display-refresh cadence, other platform, frame-rate, second-client, remote-session or stock-grade claim.',
});

/** Rederives or builds the report of a suite version (default: the current one); a retained run is checked under the version its context names. */
export function buildAnimationReport(frames, actions, seal, runId, output, priorChallenges = [], version = currentAnimationVersion) {
    const suite = animationSuites[version];
    checkImages(suite !== undefined && /^[a-f\d]{32}$/.test(runId ?? '') && frames?.length === suite.plan.length + 1 && actions?.length === suite.plan.length
        && Array.isArray(priorChallenges) && priorChallenges.length <= animationPriorCap && new Set(priorChallenges).size === priorChallenges.length);
    const builder = new AnimationReport(frames, actions, runId, output, priorChallenges, version);
    frames.forEach((frame, index) => builder.processFrame(frame, index));
    builder.checkSeal(seal);
    return { schemaVersion: 1, suiteId: animationSuiteIds[version], runId, policy: ownedAnimationPolicies[version], scope: scopes[version],
        holdMs: animationHoldMs, inputs: animationInputs(version).map(({ bytes, ...value }) => ({ ...value, byteCount: bytes.length })), priorChallenges,
        queriedPrivateModes: [5, 66, 9, 1000, 1002, 1003, 1006], kittyKeyboardFlags: 0, backgroundControls: builder.backgrounds,
        initialSnapshot: 0, restoredSnapshot: suite.plan.length, snapshots: frames, actions, seal,
        caseResults: builder.caseResults(), observations: builder.observations };
}

/** What the shared image-carrier observer and report need to know about one suite version. */
function suiteOf(version) {
    const cases = animationCasesByVersion[version], stageOfEntry = entry => cases.find(value => value.id === entry.caseId).stages.find(value => value.id === entry.stageId);
    return Object.freeze({
        plan: planOf(cases), prefix: 'kan-', relaunches: entry => entry.kind === 'read' && stageOfEntry(entry).relaunch === true, pixelDir: animationPixelPrefix, inputDir: 'animation-inputs', priorCap: animationPriorCap, recordSpan: true,
        inputs: () => animationInputs(version), action: (entry, state, runId, cursorVisible) => animationAction(entry, state, runId, cursorVisible, version), buildReport: (frames, actions, seal, runId, output, prior) => buildAnimationReport(frames, actions, seal, runId, output, prior, version),
        retainPixel: retainedAnimationPixel, captureKind: imageCaptureKind, pairMayDiffer: mayDiffer(cases), stageOfEntry,
        acceptReply: (reply, entry, states) => { animationReply(reply.toString('ascii'), stageOfEntry(entry), states.get(entry.caseId).imageId); },
        newState(states) {
            let imageId;
            do { imageId = randomInt(1, 0x100000000); } while ([...states.values()].some(value => value.imageId === imageId));
            return { imageId };
        },
        actionFacts: state => ({ imageId: state.imageId ?? null }),
        registerState(action, states) {
            checkImages(uint(action.imageId) && ![...states.values()].some(value => value.imageId === action.imageId));
            return { imageId: action.imageId };
        },
        factsMatch: (action, state) => action.imageId === (state.imageId ?? null),
    });
}
export const animationSuites = Object.freeze({ 1: suiteOf(1), 2: suiteOf(2), 3: suiteOf(3) });
export const animationSuite = animationSuites[currentAnimationVersion];

class AnimationReport extends StaticImageReport {
    constructor(frames, actions, runId, output, priorChallenges, version) {
        super(frames, actions, runId, output, priorChallenges, animationSuites[version]);
        this.cases = animationCasesByVersion[version]; this.suite = animationSuites[version];
        this.lastEnd = -Infinity; this.facts = new Map();
    }
    initialFrameFacts(frame) { this.lastEnd = animationSpanOf(frame).end; }
    validateAction(frame, index, entry) {
        const facts = super.validateAction(frame, index, entry);
        return { ...facts, actionTime: Date.parse(facts.action.ownership.at), frameTime: Date.parse(frame.ownership.at) };
    }
    /** Wall-clock facts: when a command could have taken effect, and the span the paired captures covered. */
    timingFacts(entry, stage, index, { action, actionTime, frameTime }) {
        const span = animationSpanOf(this.frames[index]);
        checkImages(frameTime <= span.start && this.lastEnd <= actionTime);
        if (stage.waitMs !== undefined) checkImages(actionTime - this.lastEnd >= stage.waitMs);
        const previousEnd = this.lastEnd;
        this.lastEnd = span.end;
        const caseFacts = this.facts.get(entry.caseId) ?? {};
        // The new App was started, and its pane showed the restored screen, within this window: a playhead it restarted could have begun anywhere in it.
        if (stage.relaunch) {
            const { relaunch } = action.ownership;
            checkImages(Date.parse(relaunch.startedAt) >= previousEnd && Date.parse(relaunch.at) <= actionTime);
            caseFacts.relaunch = { earliest: Date.parse(relaunch.startedAt), latest: Date.parse(relaunch.at) };
        }
        if (stage.reply === 'build') caseFacts.build = { earliest: actionTime, latest: frameTime };
        if (stage.reply === 'start') caseFacts.start = { earliest: actionTime, latest: frameTime };
        if (stage.reply === 'stop') caseFacts.stop = { earliest: actionTime, latest: frameTime };
        this.facts.set(entry.caseId, caseFacts);
        return { ...caseFacts, capture: span };
    }
    observe(entry, index, pixels, replyBytes, context) {
        const item = this.cases.find(value => value.id === entry.caseId), stage = this.suite.stageOfEntry(entry);
        const reply = animationReply(replyBytes.toString('ascii'), stage, context.state.imageId);
        const facts = this.timingFacts(entry, stage, index, context);
        // A pair that differs means the frame changed between its two captures. Independent timing decides what that means: where a boundary could fall
        // inside the span it is legitimate and the stage is inconclusive; where only one frame can be on screen it is an inconsistency and fails.
        const pairStable = this.frames[index].pixels[0].sha256 === this.frames[index].pixels[1].sha256;
        const timing = evaluateAnimationTiming(stage, facts);
        // A relaunch stage that follows a running animation shows whichever frame the independent timeline gives for the capture span (when the App came back varies); an ambiguous span makes the timing invalid, so the placeholder can only leave the stage inconclusive.
        const appearance = stage.appearance === 'timeline' ? timelineAppearance(facts) ?? 'root' : stage.appearance;
        const inspect = observed => inspectAnimationPixels({ baseline: this.boundPixel.image, observed: observed.image, grid: observed.details.grid,
            anchor: animationAnchor, cells: animationCellEnvelope, appearance });
        const pixel = inspect(pixels[0]);
        checkImages(pixel.controls[0].passed);
        // Bleed is timing-independent, so a pair that differs must be clean in both members.
        const other = pairStable ? pixel : inspect(pixels[1]);
        checkImages(other.controls[0].passed);
        const bleed = { ...pixel.controls[1], passed: pixel.controls[1].passed && other.controls[1].passed };
        const assertions = [...pixel.assertions, ...(pairStable || !timing.valid ? [] : [{ description: 'paired captures show one frame inside a window only one frame can occupy', passed: false }]), bleed];
        this.observations.push({ featureId: item.id + '.' + stage.id, ...this.gradeStage(item, stage, reply, assertions, timing), snapshot: index,
            actionIndex: context.action.index, reply, assertions, classification: pixel.classification, measurement: pixel.measurement,
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
        if (reply.passed === false || assertions.at(-1).passed === false) return { result: 'failed', causality, inconclusiveReason: null };
        if (inconclusiveReason) return { result: 'inconclusive', causality, inconclusiveReason };
        return { result: assertions.every(value => value.passed) ? 'passed' : 'failed', causality, inconclusiveReason };
    }
    caseResults() {
        return this.cases.map(item => {
            const stages = this.observations.filter(value => value.featureId.startsWith(item.id + '.'));
            checkImages(stages.length === item.stages.length);
            let result = 'passed';
            if (stages.some(value => value.result === 'inconclusive')) result = 'inconclusive';
            if (stages.some(value => value.result === 'failed')) result = 'failed';
            return { caseId: item.id, result, stages: stages.map(value => value.featureId) };
        });
    }
}
