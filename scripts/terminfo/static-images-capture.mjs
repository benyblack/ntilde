import { randomInt } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { OwnedCellObserver, snapshotHash, observerEndpoint, imageCaptureRequest } from './agent-observer.mjs';
import { pngHash, totalPixelLimit } from './pixels-capture.mjs';
import { staticImageCases } from './static-images-cases.mjs';
import { checkImages, imagePlan, imageAction, imageReply, imageBoundScreen, imageInputs, buildStaticImageReport, validateImageOwnership,
    imageRawReplies, validateImageCapture, stableImageContext, pristineImageBaseline } from './static-images.mjs';

/** No new action surface: worker emits bytes only after each parent ownership admission. */
export class OwnedStaticImageObserver {
    constructor({ output, priorChallenges = [], captureRpc = imageCaptureRequest, ...options }) {
        checkImages(/^NTILDE_IMG_[a-f\d]{32}$/.test(options.nonce ?? '') && Array.isArray(priorChallenges) && priorChallenges.length <= 80);
        this.output = output; this.runId = options.nonce.slice(11); this.priorChallenges = [...priorChallenges];
        this.used = new Set(priorChallenges); this.captureRpc = captureRpc; this.frames = []; this.actions = []; this.states = new Map();
        this.next = 0; this.phase = 'start'; this.totalPixels = 0;
        const facts = { ...options, includeCellDetails: true, includeCursorMode: true, includeOverline: true, includeBufferState: true };
        this.before = new OwnedCellObserver(facts); this.after = new OwnedCellObserver(facts);
    }
    get paneId() { return this.before.paneId; }
    get restored() { return this.phase === 'complete' && !this.failed; }
    action() {
        const entry = imagePlan[this.next]; checkImages(entry);
        if (entry.kind === 'read' && !this.states.has(entry.caseId)) {
            let number;
            do { number = randomInt(1, 0x100000000); } while ([...this.states.values()].some(value => value.number === number));
            this.states.set(entry.caseId, { number, firstId: null, ids: [] });
        }
        const state = this.states.get(entry.caseId) ?? {}, expected = imageAction(entry, state, this.runId, this.baseline.cursorVisible);
        return { index: this.actions.length, kind: 'images-' + entry.kind, caseId: entry.caseId ?? null, stageId: entry.stageId ?? null,
            number: state.number ?? null, firstId: state.firstId ?? null, commandHex: Buffer.from(expected.command).toString('hex'),
            commandSha256: pngHash(Buffer.from(expected.command)), skipReason: expected.skipReason };
    }
    async capture(budget) {
        const screen = await this.before.capture(budget); this.after.paneId ??= this.paneId;
        if (this.phase === 'start' && (screen.rows < 8 || screen.cols < 60)) throw Object.assign(new Error('Image fixture geometry unavailable.'), { code: 'imageGeometryUnavailable' });
        const endpoint = (this.before.endpoint ?? observerEndpoint)(this.before.appData, this.before.ownerPid);
        const first = await this.capturePixel(screen, endpoint, 0, budget);
        const second = await this.capturePixel(screen, endpoint, 1, budget);
        const pixels = [first, second], afterScreen = await this.after.capture(budget);
        checkImages(snapshotHash(screen) === snapshotHash(afterScreen) && first.sha256 === second.sha256
            && snapshotHash(first.renderDetails) === snapshotHash(second.renderDetails)
            && snapshotHash(first.imageDetails) === snapshotHash(second.imageDetails));
        return { screen, afterScreen, sha256: snapshotHash(screen), afterSha256: snapshotHash(afterScreen), pixels };
    }
    async capturePixel(screen, endpoint, side, budget) {
            const capture = await this.captureRpc(endpoint, this.paneId, 0, budget());
            const kind = this.phase === 'start' ? 'images-start' : 'images-' + imagePlan[this.next].kind;
            const { bytes, image, details, imageDetails } = validateImageCapture(capture, screen, kind);
            if (this.phase === 'start' && image.width * image.height * 62 > totalPixelLimit)
                throw Object.assign(new Error('Image paired-capture budget unavailable.'), { code: 'imageGeometryUnavailable' });
            this.totalPixels += image.width * image.height; checkImages(this.totalPixels <= totalPixelLimit);
            const context = stableImageContext(details); this.context ??= context; checkImages(context === this.context);
            const file = `image-pixels/frame-${String(this.frames.length).padStart(3, '0')}-${side}.png`;
            const pixel = { file, sha256: pngHash(bytes), byteCount: bytes.length, width: capture.width, height: capture.height,
                rows: capture.rows, cols: capture.cols, mode: capture.mode, scale: capture.scale, downscaled: false,
                inlineOmitted: false, presentationTimeMs: 0, renderDetails: details, imageDetails };
            if (!this.frames.length && side === 0) mkdirSync(join(this.output, 'image-pixels'), { mode: 0o700 });
            writeFileSync(join(this.output, file), bytes, { flag: 'wx', mode: 0o600 }); return pixel;
    }
    validateRequest(request) {
        checkImages(!this.busy && !this.failed && !this.restored);
        checkImages(Object.keys(request).every(key => ['kind', 'caseId', 'stageId', 'capture', 'captureStatus', 'tty', 'barrier', 'token', 'runId', 'workerPid'].includes(key)));
        let entry = imagePlan[this.next], kind = entry?.kind;
        if (this.phase === 'start') entry = { kind: 'start' };
        if (['start', 'admit', 'seal'].includes(this.phase)) kind = this.phase;
        if (this.phase === 'seal') entry = {};
        checkImages(request.kind === 'images-' + kind && request.caseId === entry.caseId && request.stageId === entry.stageId);
        return entry;
    }
    admit(ownership) {
        validateImageOwnership(ownership, this.frames[0].ownership, this.baseline);
        const action = { ...this.action(), ownership: structuredClone(ownership) }; this.actions.push(action); this.phase = 'capture';
        return structuredClone(action);
    }
    sealCapture(request, ownership) {
        validateImageOwnership(ownership, this.frames[0].ownership, this.baseline);
        this.seal = { ...structuredClone(request.captureStatus), ownership: structuredClone(ownership) };
        this.report = buildStaticImageReport(this.frames, this.actions, this.seal, this.runId, this.output, this.priorChallenges);
        this.phase = 'complete'; return { sealed: true };
    }
    acceptRaw(request, entry) {
        const argb = entry.kind === 'finish' ? this.initialBackground : 0xff101010;
        const { replyBytes: reply } = imageRawReplies(request.capture, this.used, argb);
        if (entry.kind !== 'read' || this.actions.at(-1).skipReason) { checkImages(reply.length === 0); return; }
        const item = staticImageCases.find(value => value.id === entry.caseId), stage = item.stages.find(value => value.id === entry.stageId);
        const state = this.states.get(entry.caseId), result = imageReply(reply.toString('ascii'), item, stage, state);
        if (result.id !== null) {
            state.ids.push(result.id);
            if (['store', 'store-first'].includes(stage.id)) state.firstId = result.id;
        }
    }
    acceptPair(pair, entry) {
        if (entry.kind !== 'read') checkImages(pair.pixels.every(value => value.imageDetails.totalPlacements === 0));
        if (this.phase !== 'start') {
            checkImages(snapshotHash(pair.screen) === snapshotHash(entry.kind === 'finish' ? this.baseline : this.bound));
            if (entry.kind === 'finish') checkImages(snapshotHash(pair.pixels[0].renderDetails) === snapshotHash(this.initialDetails));
            return;
        }
        checkImages(pristineImageBaseline(pair.screen) && pair.screen.rows >= 8 && pair.screen.cols >= 60);
        checkImages(pair.pixels[0].width * pair.pixels[0].height * 62 <= totalPixelLimit);
        this.baseline = pair.screen; this.bound = imageBoundScreen(pair.screen, this.runId);
        this.initialBackground = pair.pixels[0].renderDetails.theme.backgroundArgb;
        this.initialDetails = pair.pixels[0].renderDetails;
        mkdirSync(join(this.output, 'image-inputs'), { mode: 0o700 });
        for (const input of imageInputs()) writeFileSync(join(this.output, input.file), input.bytes, { flag: 'wx', mode: 0o600 });
    }
    async handle(request, budget, ownership) {
        const entry = this.validateRequest(request);
        this.busy = true; this.lastAttempt = { kind: request.kind, caseId: request.caseId ?? null, stageId: request.stageId ?? null,
            capture: structuredClone(request.capture ?? null) };
        try {
            if (this.phase === 'admit') return this.admit(ownership);
            if (this.phase === 'seal') return this.sealCapture(request, ownership);
            if (this.phase !== 'start') this.acceptRaw(request, entry);
            const pair = await this.capture(budget); this.lastAttempt.pair = structuredClone(pair);
            validateImageOwnership(ownership, this.frames[0]?.ownership ?? ownership, pair.screen);
            this.acceptPair(pair, entry);
            this.frames.push({ index: this.frames.length, kind: request.kind, paneId: this.paneId, ...pair,
                caseId: request.caseId ?? null, stageId: request.stageId ?? null,
                ownership: structuredClone(ownership),
                actionIndex: this.phase === 'start' ? null : this.actions.at(-1).index, capture: structuredClone(request.capture ?? null) });
            if (this.phase === 'start') this.phase = 'admit';
            else { this.next++; this.phase = this.next === imagePlan.length ? 'seal' : 'admit'; }
            return { paneId: this.paneId, screen: pair.screen, priorChallenges: this.priorChallenges };
        } catch (error) { this.failed = true; this.lastAttempt.error = String(error.message ?? error); throw error; }
        finally { this.busy = false; }
    }
}
