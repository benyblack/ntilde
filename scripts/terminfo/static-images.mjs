import { readFileSync, lstatSync } from 'node:fs';
import { join } from 'node:path';
import { snapshotHash, screenSnapshot } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { validateInputBytes, inputModeReplyHex, defaultInputModes, inputModeQueries as routedInputModeQueries } from './input-state.mjs';
import { staticImageCases, staticImageCommand, staticImageAnchor, staticImageCellEnvelope, staticImageSuiteId, parseStaticKittyReply } from './static-images-cases.mjs';
import { staticImageInput, staticSixel, staticSixelSha256 } from './static-images-inputs.mjs';
import { inspectStaticImagePixels } from './static-images-oracle.mjs';
import { pngHash, stablePixelContext, totalPixelLimit } from './pixels-capture.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';
import { parseColorReply } from './color-query.mjs';
import { validateColorCapture, pristineColorTheme } from './color-capture.mjs';

export const ownedStaticImagePolicy = 'owned-static-images-paired-raw-render-v1';
export const imageModeQueries = '\x1b]11;?\x1b\\\x1b[?5$p' + routedInputModeQueries;
export const imageModeReplyHex = Buffer.from('\x1b[?5;2$y').toString('hex') + inputModeReplyHex(defaultInputModes);
export const checkImages = value => { if (!value) throw new Error('Invalid or incomplete owned static-image evidence.'); };
export const imagePlan = Object.freeze([
    { kind: 'bind' }, ...staticImageCases.flatMap(item => [...item.stages.map(stage => ({ kind: 'read', caseId: item.id, stageId: stage.id })),
        { kind: 'restore', caseId: item.id }]), { kind: 'finish' },
].map(Object.freeze));
export const imagePosition = runId => '\x1b[0m\x1b[48;2;16;16;16m\x1b[HNTILDE_IMG_' + runId + '\x1b[5;4H\x1b[?25l';
export const imageSetup = runId => '\x1bc\x1b]11;#101010\x1b\\\x1b[?1049h\x1b[48;2;16;16;16m\x1b[2J' + imagePosition(runId);
export const imageRestore = '\x1bc';
export function imageBoundScreen(initial, runId) {
    const screen = structuredClone(initial);
    screen.lines[0] = 'NTILDE_IMG_' + runId; screen.cursorRow = 4; screen.cursorCol = 3; screen.cursorVisible = false;
    screen.bufferState.alternateScreen = true;
    screen.cellDetails = [{ ...screen.cellDetails[0], count: screen.rows * screen.cols, flags: 256, backgroundArgb: 0xff101010 }];
    return screen;
}
export function imageInputs() {
    return [...['opaque', 'alpha', 'reversed'].flatMap(source => ['rgba', 'png'].map(format => {
        const input = staticImageInput(source, format);
        return { file: `image-inputs/${source}.${format}`, bytes: input.bytes, sha256: input.sha256 };
    })), { file: 'image-inputs/opaque.sixel', bytes: Buffer.from(staticSixel), sha256: staticSixelSha256 }];
}
/** Missing/error replies are semantic negatives; mixed or uncorrelated bytes invalidate the experiment. */
export function imageReply(raw, item, stage, state) {
    if (!raw) return { passed: stage.reply === null, id: null, status: null };
    checkImages(item.id.startsWith('kitty.') && typeof raw === 'string' && raw.length <= 512);
    const { fields, status } = parseStaticKittyReply(raw);
    checkImages(/^(OK|E[A-Z\d]+(?::[ -~]*)?)$/.test(status));
    const old = ['place-old-id', 'delete-old-id'].includes(stage.id);
    checkImages(old ? fields.I === undefined && fields.i === state.firstId : fields.I === state.number);
    const placed = ['place', 'place-old', 'missing'].includes(stage.reply);
    checkImages(placed ? fields.p === 1 : fields.p === undefined);
    const ok = status === 'OK';
    if (ok && stage.reply === 'allocate') checkImages(fields.i);
    if (ok && ['place', 'place-old'].includes(stage.reply)) checkImages(fields.i === (old ? state.firstId : state.ids.at(-1)));
    const reused = stage.reply === 'allocate' && state.ids.includes(fields.i);
    const passed = imageReplyStatus(stage, status, reused);
    return { passed, id: ok && stage.reply === 'allocate' && !reused ? fields.i : null, status };
}
function imageReplyStatus(stage, status, reused) {
    if (stage.reply === 'missing') return /^ENOENT(?::|$)/.test(status);
    return stage.reply !== null && status === 'OK' && !reused;
}
function imageWitnessStages(item, stage) {
    const index = item.stages.findIndex(value => value.id === stage.id);
    if (index === 0) return [];
    if (stage.id.startsWith('store')) return ['blank'];
    if (item.id === 'kitty.number-reuse' && stage.id === 'place-newest') return ['blank', 'store-first', 'store-second'];
    return [...new Set(['blank', item.stages[index - 1].id])];
}
export function imageAction(entry, state, runId) {
    if (entry.kind === 'bind' || entry.kind === 'restore') return { command: imageSetup(runId), skipReason: null };
    if (entry.kind === 'finish') return { command: imageRestore, skipReason: null };
    const reason = imagePrerequisite(entry, state);
    if (reason) return { command: imagePosition(runId), skipReason: reason };
    return { command: imagePosition(runId) + staticImageCommand(entry.caseId, entry.stageId, state) + imagePosition(runId), skipReason: null };
}
function imagePrerequisite(entry, state) {
    if (!entry.caseId.startsWith('kitty.') || entry.stageId === 'blank' || entry.stageId.startsWith('store')) return null;
    if (entry.caseId === 'kitty.numbered-lifecycle') return state.firstId === null ? 'required allocation did not return a usable image id' : null;
    return state.ids.length === 2 && state.firstId === state.ids[0] ? null : 'number reuse requires both distinct successful allocations';
}
export function validateImageCapture(result, screen, kind) {
    const value = validateColorCapture(result, screen, null), theme = value.details.theme;
    const original = ['images-start', 'images-finish'].includes(kind);
    checkImages(pristineColorTheme({ ...theme, backgroundOverride: false }) && theme.backgroundOverride === !original
        && (original || theme.backgroundArgb === 0xff101010)
        && result.syntheticSelection == null && value.details.syntheticSelection == null);
    return value;
}
export function stableImageContext(details) {
    const { backgroundArgb, backgroundOverride, cursorTextArgb, effectiveSelectionBackgroundArgb, effectiveSelectionForegroundArgb, ...theme } = details.theme;
    return stablePixelContext({ ...details, theme });
}
export function imageBackgroundReply(argb) {
    return '\x1b]11;rgb:' + [16, 8, 0].map(shift => (((argb >>> shift) & 255) * 257).toString(16).padStart(4, '0')).join('/') + '\x1b\\';
}
export function imageRawReplies(capture, used, backgroundArgb) {
    const raw = capture?.hex; checkImages(typeof raw === 'string' && raw.endsWith(imageModeReplyHex));
    validateInputBytes(capture, raw, used);
    const bytes = Buffer.from(raw.slice(0, -imageModeReplyHex.length), 'hex'); checkImages(bytes.every(byte => byte <= 127));
    const text = bytes.toString('ascii'), start = text.lastIndexOf('\x1b]11;'); checkImages(start >= 0);
    const colorRaw = text.slice(start), backgroundQuery = parseColorReply(colorRaw, { code: 11 });
    checkImages(colorRaw.toLowerCase() === imageBackgroundReply(backgroundArgb));
    return { replyBytes: Buffer.from(text.slice(0, start), 'ascii'), backgroundQuery };
}
export function retainedImagePixel(output, pixel, screen, kind) {
    checkImages(/^image-pixels\/frame-\d{3}-[01]\.png$/.test(pixel?.file ?? ''));
    const path = join(output, pixel.file), stat = lstatSync(path);
    checkImages(stat.isFile() && !stat.isSymbolicLink() && stat.size <= 16 * 1024 * 1024);
    const bytes = readFileSync(path); checkImages(pngHash(bytes) === pixel.sha256);
    return validateImageCapture({ ...pixel, pngBase64: bytes.toString('base64') }, screen, kind);
}
export function validateImageOwnership(actual, original, geometry) {
    checkImages(actual && original && actual.tty === original.tty && /^\/dev\/ttys\d+$/.test(actual.tty)
        && typeof actual.at === 'string' && new Date(actual.at).toISOString() === actual.at
        && actual.terminalOutput?.tool === '/bin/stty' && actual.terminalOutput.outputProcessing === false);
    for (const side of ['app', 'worker']) checkImages(actual[side] && original[side]
        && ['pid', 'ppid', 'uid', 'tty', 'executable'].every(key => actual[side][key] === original[side][key]));
    checkImages(Number.isSafeInteger(actual.app.pid) && actual.app.pid > 1 && actual.worker.ppid === actual.app.pid
        && Number.isSafeInteger(actual.worker.pid) && actual.worker.pid > 1 && actual.worker.uid === actual.app.uid
        && actual.worker.tty === actual.tty.slice(5));
    validateOutputGeometry(actual.terminalOutput.description, geometry.rows, geometry.cols);
}
export function buildStaticImageReport(frames, actions, seal, runId, output, priorChallenges = []) {
    checkImages(/^[a-f\d]{32}$/.test(runId ?? '') && frames?.length === 31 && actions?.length === 30
        && Array.isArray(priorChallenges) && priorChallenges.length <= 80 && new Set(priorChallenges).size === priorChallenges.length);
    const builder = new StaticImageReport(frames, actions, runId, output, priorChallenges);
    frames.forEach((frame, index) => builder.processFrame(frame, index));
    builder.checkSeal(seal);
    return { schemaVersion: 1, suiteId: staticImageSuiteId, runId, policy: ownedStaticImagePolicy,
        scope: 'Five bounded static image cases; independently captured raw replies, paired original offscreen PNGs and complete cells/modes. No animation, placeholders, live desktop timing, native input, other-platform runtime or stock-grade claim.',
        inputs: imageInputs().map(({ bytes, ...value }) => ({ ...value, byteCount: bytes.length })), priorChallenges,
        queriedPrivateModes: [5, 66, 9, 1000, 1002, 1003, 1006], kittyKeyboardFlags: 0, backgroundControls: builder.backgrounds,
        initialSnapshot: 0, restoredSnapshot: 30, snapshots: frames, actions, seal,
        caseResults: builder.caseResults(), observations: builder.observations };
}
class StaticImageReport {
    constructor(frames, actions, runId, output, priorChallenges) {
        this.frames = frames; this.actions = actions; this.runId = runId; this.output = output;
        this.initial = frames[0].screen; this.paneId = frames[0].paneId; this.bound = imageBoundScreen(this.initial, runId);
        checkImages(pristineEditing(this.initial) && this.initial.rows >= 8 && this.initial.cols >= 60);
        this.used = new Set(priorChallenges); this.states = new Map(); this.observations = []; this.backgrounds = []; this.total = 0; this.lastTime = -Infinity;
    }
    validateFrame(frame, index, entry) {
        validateImageOwnership(frame.ownership, this.frames[0].ownership, this.initial);
        checkImages(frame.index === index && frame.kind === 'images-' + entry.kind && frame.paneId === this.paneId
            && frame.caseId === (entry.caseId ?? null) && frame.stageId === (entry.stageId ?? null));
        for (const screen of [frame.screen, frame.afterScreen]) screenSnapshot(screen, false, true, true, true, true);
        const expectedScreen = ['start', 'finish'].includes(entry.kind) ? this.initial : this.bound;
        checkImages(frame.sha256 === snapshotHash(frame.screen) && frame.afterSha256 === snapshotHash(frame.afterScreen)
            && snapshotHash(frame.screen) === snapshotHash(expectedScreen) && snapshotHash(frame.afterScreen) === snapshotHash(expectedScreen));
    }
    readPixels(frame, index) {
        checkImages(frame.pixels?.length === 2);
        const pixels = frame.pixels.map((pixel, side) => {
            checkImages(pixel.file === `image-pixels/frame-${String(index).padStart(3, '0')}-${side}.png`);
            const value = retainedImagePixel(this.output, pixel, frame.screen, frame.kind); this.total += value.image.width * value.image.height;
            const stable = stableImageContext(value.details); this.context ??= stable; checkImages(stable === this.context);
            return value;
        });
        checkImages(this.total <= totalPixelLimit && frame.pixels[0].sha256 === frame.pixels[1].sha256
            && snapshotHash(frame.pixels[0].renderDetails) === snapshotHash(frame.pixels[1].renderDetails));
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
            checkImages(Number.isSafeInteger(action.number) && action.number >= 1 && action.number <= 0xffffffff
                && ![...this.states.values()].some(value => value.number === action.number));
            this.states.set(entry.caseId, { number: action.number, firstId: null, ids: [] });
        }
        const state = this.states.get(entry.caseId) ?? {}, expected = imageAction(entry, state, this.runId);
        checkImages(action.commandHex === Buffer.from(expected.command).toString('hex') && action.commandSha256 === pngHash(Buffer.from(expected.command))
            && action.skipReason === expected.skipReason && action.number === (state.number ?? null) && action.firstId === (state.firstId ?? null));
        return { action, state, expected };
    }
    readReply(frame) {
        const argb = frame.kind === 'images-finish' ? this.initialBackground : 0xff101010;
        const { replyBytes, backgroundQuery } = imageRawReplies(frame.capture, this.used, argb);
        this.backgrounds.push({ snapshot: frame.index, argb, query: backgroundQuery });
        return replyBytes;
    }
    checkControl(entry, frame, pixels, replyBytes) {
        checkImages(replyBytes.length === 0);
        if (entry.kind === 'bind') this.boundPixel = pixels[0];
        if (entry.kind === 'restore') checkImages(frame.pixels[0].sha256 === pngHash(this.boundPixel.bytes));
        if (entry.kind === 'finish') checkImages(frame.pixels[0].sha256 === this.initialPixel
            && snapshotHash(frame.pixels[0].renderDetails) === snapshotHash(this.frames[0].pixels[0].renderDetails));
    }
    observe(entry, index, pixels, replyBytes, { action, state, expected }) {
        const item = staticImageCases.find(value => value.id === entry.caseId);
        const stage = item.stages.find(value => value.id === entry.stageId);
        const reply = expected.skipReason ? { passed: false, id: null, status: null } : imageReply(replyBytes.toString('ascii'), item, stage, state);
        if (expected.skipReason) checkImages(replyBytes.length === 0);
        if (reply.id !== null) {
            state.ids.push(reply.id);
            if (['store', 'store-first'].includes(stage.id)) state.firstId = reply.id;
        }
        const pixel = inspectStaticImagePixels({ baseline: this.boundPixel.image, observed: pixels[0].image, grid: pixels[0].details.grid,
            anchor: staticImageAnchor, cells: staticImageCellEnvelope, geometry: item.geometry,
            sourceId: stage.appearance === 'reversed' ? 'reversed' : item.sourceId, visible: stage.appearance !== 'absent' });
        checkImages(pixel.controls[0].passed);
        const assertions = [...pixel.assertions, pixel.controls[1]];
        const grade = this.gradeStage(item, stage, expected, reply, assertions);
        this.observations.push({ featureId: item.id + '.' + stage.id, ...grade,
        snapshot: index, actionIndex: action.index, reply, assertions, measurement: pixel.measurement, skipReason: expected.skipReason });
    }
    gradeStage(item, stage, expected, reply, assertions) {
        const causality = imageWitnessStages(item, stage).map(id => {
            const featureId = item.id + '.' + id, witness = this.observations.find(value => value.featureId === featureId);
            checkImages(witness);
            return { featureId, snapshot: witness.snapshot, result: witness.result, passed: witness.result === 'passed' };
        });
        const missing = causality.filter(value => !value.passed);
        let inconclusiveReason = expected.skipReason;
        if (!inconclusiveReason && missing.length) inconclusiveReason = 'requires passed prior witness: '
            + missing.map(value => value.featureId + ' (' + value.result + ')').join(', ');
        let result = 'inconclusive';
        if (!inconclusiveReason) result = reply.passed && assertions.every(value => value.passed) ? 'passed' : 'failed';
        return { result, causality, inconclusiveReason };
    }
    caseResults() {
        return staticImageCases.map(item => {
            const stages = this.observations.filter(value => value.featureId.startsWith(item.id + '.'));
            checkImages(stages.length === item.stages.length);
            let result = 'passed';
            if (stages.some(value => value.result === 'inconclusive')) result = 'inconclusive';
            if (stages.some(value => value.result === 'failed')) result = 'failed';
            return { caseId: item.id, result, stages: stages.map(value => value.featureId) };
        });
    }
    processFrame(frame, index) {
        const entry = index ? imagePlan[index - 1] : { kind: 'start' };
        this.validateFrame(frame, index, entry); const pixels = this.readPixels(frame, index);
        if (!index) {
            checkImages(frame.capture === null && frame.actionIndex === null); this.initialPixel = frame.pixels[0].sha256;
            this.initialBackground = pixels[0].details.theme.backgroundArgb;
            this.lastTime = Date.parse(frame.ownership.at); return;
        }
        const facts = this.validateAction(frame, index, entry), replyBytes = this.readReply(frame);
        if (entry.kind !== 'read') this.checkControl(entry, frame, pixels, replyBytes);
        else this.observe(entry, index, pixels, replyBytes, facts);
    }
    checkSeal(seal) {
        const last = this.frames.at(-1).capture;
        validateImageOwnership(seal?.ownership, this.frames[0].ownership, this.initial);
        checkImages(this.lastTime <= Date.parse(seal.ownership.at));
        checkImages(seal?.failed === false && seal.disposed === true && seal.attempts === 30 && seal.lastAttempt?.completed === true
        && seal.lastAttempt.error === null && seal.lastAttempt.fenceRequested === true
        && seal.lastAttempt.hex === last.hex + Buffer.from(last.barrier).toString('hex')
        && snapshotHash(seal.lastAttempt.nonces) === snapshotHash(last.nonces));
        for (const input of imageInputs()) {
            const stat = lstatSync(join(this.output, input.file)); checkImages(stat.isFile() && !stat.isSymbolicLink() && stat.size === input.bytes.length);
            checkImages(pngHash(readFileSync(join(this.output, input.file))) === input.sha256);
        }
    }
}
