import { readFileSync, lstatSync } from 'node:fs';
import { join } from 'node:path';
import { snapshotHash, screenSnapshot } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { validateInputBytes, inputModeReplyHex, defaultInputModes, inputModeQueries as routedInputModeQueries } from './input-state.mjs';
import { staticImageCases, staticImageCommand, staticImageAnchor, staticImageCellEnvelope, staticImageSuiteId } from './static-images-cases.mjs';
import { staticImageInput, staticSixel, staticSixelSha256 } from './static-images-inputs.mjs';
import { inspectStaticImagePixels } from './static-images-oracle.mjs';
import { pngHash, stablePixelContext, totalPixelLimit, validatePixelCapture } from './pixels-capture.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';

export const ownedStaticImagePolicy = 'owned-static-images-paired-raw-render-v1';
export const imageModeQueries = '\x1b[?5$p' + routedInputModeQueries;
export const imageModeReplyHex = Buffer.from('\x1b[?5;2$y').toString('hex') + inputModeReplyHex(defaultInputModes);
export const checkImages = value => { if (!value) throw new Error('Invalid or incomplete owned static-image evidence.'); };
export const imagePlan = Object.freeze([
    { kind: 'bind' }, ...staticImageCases.flatMap(item => [...item.stages.map(stage => ({ kind: 'read', caseId: item.id, stageId: stage.id })),
        { kind: 'restore', caseId: item.id }]), { kind: 'finish' },
].map(Object.freeze));
export const imagePosition = runId => '\x1b[0m\x1b[48;2;16;16;16m\x1b[HNTILDE_IMG_' + runId + '\x1b[5;4H\x1b[?25l';
export const imageSetup = runId => '\x1bc\x1b[?1049h\x1b[48;2;16;16;16m\x1b[2J' + imagePosition(runId);
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
    const match = /^\x1b_G([^;]+);(OK|E[A-Z0-9]+(?::[ -~]*)?)\x1b\\$/.exec(raw);
    checkImages(match);
    const fields = {};
    for (const part of match[1].split(',')) {
        const field = /^(i|I|p)=([1-9][0-9]{0,9})$/.exec(part);
        checkImages(field && !Object.hasOwn(fields, field[1]) && Number(field[2]) <= 0xffffffff);
        fields[field[1]] = Number(field[2]);
    }
    const old = ['place-old-id', 'delete-old-id'].includes(stage.id);
    checkImages(old ? fields.I === undefined && fields.i === state.firstId : fields.I === state.number);
    const placed = ['place', 'place-old', 'missing'].includes(stage.reply);
    checkImages(placed ? fields.p === 1 : fields.p === undefined);
    const ok = match[2] === 'OK';
    if (ok && stage.reply === 'allocate') checkImages(fields.i);
    if (ok && ['place', 'place-old'].includes(stage.reply)) checkImages(fields.i === (old ? state.firstId : state.ids.at(-1)));
    const reused = stage.reply === 'allocate' && state.ids.includes(fields.i);
    const passed = stage.reply === 'missing' ? /^ENOENT(?::|$)/.test(match[2])
        : stage.reply !== null && ok && !reused;
    return { passed, id: ok && stage.reply === 'allocate' && !reused ? fields.i : null, status: match[2] };
}
export function imageAction(entry, state, runId) {
    if (entry.kind === 'bind' || entry.kind === 'restore') return { command: imageSetup(runId), skipReason: null };
    if (entry.kind === 'finish') return { command: imageRestore, skipReason: null };
    const item = staticImageCases.find(value => value.id === entry.caseId);
    const needsOld = ['place-old-id', 'delete-old-id'].includes(entry.stageId);
    if (needsOld && state.firstId === null) return { command: imagePosition(runId), skipReason: 'first allocation did not return a usable image id' };
    return { command: imagePosition(runId) + staticImageCommand(entry.caseId, entry.stageId, state) + imagePosition(runId), skipReason: null };
}
export function retainedImagePixel(output, pixel, screen) {
    checkImages(/^image-pixels\/frame-\d{3}-[01]\.png$/.test(pixel?.file ?? ''));
    const path = join(output, pixel.file), stat = lstatSync(path);
    checkImages(stat.isFile() && !stat.isSymbolicLink() && stat.size <= 16 * 1024 * 1024);
    const bytes = readFileSync(path); checkImages(pngHash(bytes) === pixel.sha256);
    return validatePixelCapture({ ...pixel, pngBase64: bytes.toString('base64') }, screen, 0);
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
    const initial = frames[0].screen, paneId = frames[0].paneId, bound = imageBoundScreen(initial, runId);
    checkImages(pristineEditing(initial) && initial.rows >= 8 && initial.cols >= 60);
    const used = new Set(priorChallenges), states = new Map(), observations = [];
    let context, initialPixel, boundPixel, total = 0, lastTime = -Infinity;
    for (let index = 0; index < frames.length; index++) {
        const frame = frames[index], entry = index ? imagePlan[index - 1] : { kind: 'start' };
        validateImageOwnership(frame.ownership, frames[0].ownership, initial);
        if (index) {
            const actionTime = Date.parse(actions[index - 1]?.ownership?.at), frameTime = Date.parse(frame.ownership.at);
            checkImages(lastTime <= actionTime && actionTime <= frameTime);
        }
        lastTime = Date.parse(frame.ownership.at);
        checkImages(frame.index === index && frame.kind === 'images-' + entry.kind && frame.paneId === paneId
            && frame.caseId === (entry.caseId ?? null) && frame.stageId === (entry.stageId ?? null));
        for (const screen of [frame.screen, frame.afterScreen]) screenSnapshot(screen, false, true, true, true, true);
        const expectedScreen = ['start', 'finish'].includes(entry.kind) ? initial : bound;
        checkImages(frame.sha256 === snapshotHash(frame.screen) && frame.afterSha256 === snapshotHash(frame.afterScreen)
            && snapshotHash(frame.screen) === snapshotHash(expectedScreen) && snapshotHash(frame.afterScreen) === snapshotHash(expectedScreen));
        checkImages(frame.pixels?.length === 2);
        const pixels = frame.pixels.map((pixel, side) => {
            checkImages(pixel.file === `image-pixels/frame-${String(index).padStart(3, '0')}-${side}.png`);
            const value = retainedImagePixel(output, pixel, frame.screen); total += value.image.width * value.image.height;
            const stable = stablePixelContext(value.details); context ??= stable; checkImages(stable === context);
            return value;
        });
        checkImages(total <= totalPixelLimit && frame.pixels[0].sha256 === frame.pixels[1].sha256);
        if (!index) { checkImages(frame.capture === null && frame.actionIndex === null); initialPixel = frame.pixels[0].sha256; continue; }
        const action = actions[index - 1];
        validateImageOwnership(action.ownership, frames[0].ownership, initial);
        checkImages(action.index === index - 1 && frame.actionIndex === action.index && action.kind === frame.kind
            && action.caseId === frame.caseId && action.stageId === frame.stageId);
        const item = staticImageCases.find(value => value.id === entry.caseId);
        if (entry.kind === 'read' && !states.has(item.id)) {
            checkImages(Number.isSafeInteger(action.number) && action.number >= 1 && action.number <= 0xffffffff
                && ![...states.values()].some(value => value.number === action.number));
            states.set(item.id, { number: action.number, firstId: null, ids: [] });
        }
        const state = states.get(entry.caseId) ?? {};
        const expected = imageAction(entry, state, runId);
        checkImages(action.commandHex === Buffer.from(expected.command).toString('hex') && action.commandSha256 === pngHash(Buffer.from(expected.command))
            && action.skipReason === expected.skipReason && action.number === (state.number ?? null) && action.firstId === (state.firstId ?? null));
        const modeHex = imageModeReplyHex, raw = frame.capture?.hex;
        checkImages(typeof raw === 'string' && raw.endsWith(modeHex));
        validateInputBytes(frame.capture, raw, used);
        const replyBytes = Buffer.from(raw.slice(0, -modeHex.length), 'hex');
        checkImages(replyBytes.every(byte => byte <= 127));
        if (entry.kind !== 'read') {
            checkImages(replyBytes.length === 0);
            if (entry.kind === 'bind') boundPixel = pixels[0];
            if (entry.kind === 'restore') checkImages(frame.pixels[0].sha256 === pngHash(boundPixel.bytes));
            if (entry.kind === 'finish') checkImages(frame.pixels[0].sha256 === initialPixel);
            continue;
        }
        const stage = item.stages.find(value => value.id === entry.stageId);
        const reply = expected.skipReason ? { passed: false, id: null, status: null } : imageReply(replyBytes.toString('ascii'), item, stage, state);
        if (expected.skipReason) checkImages(replyBytes.length === 0);
        if (reply.id !== null) { state.ids.push(reply.id); state.firstId ??= reply.id; }
        const pixel = inspectStaticImagePixels({ baseline: boundPixel.image, observed: pixels[0].image, grid: pixels[0].details.grid,
            anchor: staticImageAnchor, cells: staticImageCellEnvelope, geometry: item.geometry,
            sourceId: stage.appearance === 'reversed' ? 'reversed' : item.sourceId, visible: stage.appearance !== 'absent' });
        checkImages(pixel.controls[0].passed);
        const assertions = [...pixel.assertions, pixel.controls[1]];
        observations.push({ featureId: item.id + '.' + stage.id, result: expected.skipReason ? 'inconclusive'
            : reply.passed && assertions.every(value => value.passed) ? 'passed' : 'failed',
        snapshot: index, actionIndex: action.index, reply, assertions, measurement: pixel.measurement, skipReason: expected.skipReason });
    }
    const last = frames.at(-1).capture;
    validateImageOwnership(seal?.ownership, frames[0].ownership, initial);
    checkImages(lastTime <= Date.parse(seal.ownership.at));
    checkImages(seal?.failed === false && seal.disposed === true && seal.attempts === 30 && seal.lastAttempt?.completed === true
        && seal.lastAttempt.error === null && seal.lastAttempt.fenceRequested === true
        && seal.lastAttempt.hex === last.hex + Buffer.from(last.barrier).toString('hex')
        && snapshotHash(seal.lastAttempt.nonces) === snapshotHash(last.nonces));
    for (const input of imageInputs()) {
        const stat = lstatSync(join(output, input.file)); checkImages(stat.isFile() && !stat.isSymbolicLink() && stat.size === input.bytes.length);
        checkImages(pngHash(readFileSync(join(output, input.file))) === input.sha256);
    }
    return { schemaVersion: 1, suiteId: staticImageSuiteId, runId, policy: ownedStaticImagePolicy,
        scope: 'Five bounded static image cases; independently captured raw replies, paired original offscreen PNGs and complete cells/modes. No animation, placeholders, live desktop timing, native input, other-platform runtime or stock-grade claim.',
        inputs: imageInputs().map(({ bytes, ...value }) => ({ ...value, byteCount: bytes.length })), priorChallenges,
        queriedPrivateModes: [5, 66, 9, 1000, 1002, 1003, 1006], kittyKeyboardFlags: 0,
        initialSnapshot: 0, restoredSnapshot: 30, snapshots: frames, actions, seal, observations };
}
