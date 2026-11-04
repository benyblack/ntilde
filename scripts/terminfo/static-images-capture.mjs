import { randomInt } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { OwnedCellObserver, snapshotHash, observerEndpoint, captureRequest } from './agent-observer.mjs';
import { validatePixelCapture, pngHash, stablePixelContext, totalPixelLimit } from './pixels-capture.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { validateInputBytes } from './input-state.mjs';
import { staticImageCases } from './static-images-cases.mjs';
import { checkImages, imagePlan, imageAction, imageReply, imageBoundScreen, imageInputs, buildStaticImageReport, validateImageOwnership, imageModeReplyHex } from './static-images.mjs';

/** No new action surface: worker emits bytes only after each parent ownership admission. */
export class OwnedStaticImageObserver {
    constructor({ output, priorChallenges = [], captureRpc = captureRequest, ...options }) {
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
        const state = this.states.get(entry.caseId) ?? {}, expected = imageAction(entry, state, this.runId);
        return { index: this.actions.length, kind: 'images-' + entry.kind, caseId: entry.caseId ?? null, stageId: entry.stageId ?? null,
            number: state.number ?? null, firstId: state.firstId ?? null, commandHex: Buffer.from(expected.command).toString('hex'),
            commandSha256: pngHash(Buffer.from(expected.command)), skipReason: expected.skipReason };
    }
    async capture(budget) {
        const screen = await this.before.capture(budget); this.after.paneId ??= this.paneId;
        if (this.phase === 'start' && (screen.rows < 8 || screen.cols < 60)) throw Object.assign(new Error('Image fixture geometry unavailable.'), { code: 'imageGeometryUnavailable' });
        const endpoint = (this.before.endpoint ?? observerEndpoint)(this.before.appData, this.before.ownerPid);
        const pixels = [];
        for (let side = 0; side < 2; side++) {
            const capture = await this.captureRpc(endpoint, this.paneId, 0, budget());
            const { bytes, image, details } = validatePixelCapture(capture, screen, 0);
            if (this.phase === 'start' && image.width * image.height * 62 > totalPixelLimit)
                throw Object.assign(new Error('Image paired-capture budget unavailable.'), { code: 'imageGeometryUnavailable' });
            this.totalPixels += image.width * image.height; checkImages(this.totalPixels <= totalPixelLimit);
            const context = stablePixelContext(details); this.context ??= context; checkImages(context === this.context);
            const file = `image-pixels/frame-${String(this.frames.length).padStart(3, '0')}-${side}.png`;
            const pixel = { file, sha256: pngHash(bytes), byteCount: bytes.length, width: capture.width, height: capture.height,
                rows: capture.rows, cols: capture.cols, mode: capture.mode, scale: capture.scale, downscaled: false,
                inlineOmitted: false, presentationTimeMs: 0, renderDetails: details };
            if (!this.frames.length && side === 0) mkdirSync(join(this.output, 'image-pixels'), { mode: 0o700 });
            writeFileSync(join(this.output, file), bytes, { flag: 'wx', mode: 0o600 }); pixels.push(pixel);
        }
        const afterScreen = await this.after.capture(budget);
        checkImages(snapshotHash(screen) === snapshotHash(afterScreen) && pixels[0].sha256 === pixels[1].sha256);
        return { screen, afterScreen, sha256: snapshotHash(screen), afterSha256: snapshotHash(afterScreen), pixels };
    }
    async handle(request, budget, ownership) {
        checkImages(!this.busy && !this.failed && !this.restored);
        checkImages(Object.keys(request).every(key => ['kind', 'caseId', 'stageId', 'capture', 'captureStatus', 'tty', 'barrier', 'token', 'runId', 'workerPid'].includes(key)));
        const entry = this.phase === 'start' ? { kind: 'start' } : imagePlan[this.next];
        checkImages(request.kind === 'images-' + (this.phase === 'admit' ? 'admit' : this.phase === 'seal' ? 'seal' : entry.kind)
            && request.caseId === (this.phase === 'seal' ? undefined : entry.caseId)
            && request.stageId === (this.phase === 'seal' ? undefined : entry.stageId));
        this.busy = true; this.lastAttempt = { kind: request.kind, caseId: request.caseId ?? null, stageId: request.stageId ?? null,
            capture: structuredClone(request.capture ?? null) };
        try {
            if (this.phase === 'admit') {
                validateImageOwnership(ownership, this.frames[0].ownership, this.baseline);
                const action = { ...this.action(), ownership: structuredClone(ownership) }; this.actions.push(action); this.phase = 'capture';
                return structuredClone(action);
            }
            if (this.phase === 'seal') {
                validateImageOwnership(ownership, this.frames[0].ownership, this.baseline);
                this.seal = { ...structuredClone(request.captureStatus), ownership: structuredClone(ownership) };
                this.report = buildStaticImageReport(this.frames, this.actions, this.seal, this.runId, this.output, this.priorChallenges);
                this.phase = 'complete'; return { sealed: true };
            }
            if (this.phase !== 'start') {
                const raw = request.capture?.hex, modes = imageModeReplyHex;
                checkImages(typeof raw === 'string' && raw.endsWith(modes)); validateInputBytes(request.capture, raw, this.used);
                const reply = Buffer.from(raw.slice(0, -modes.length), 'hex'); checkImages(reply.every(byte => byte <= 127));
                if (entry.kind === 'read') {
                    const action = this.actions.at(-1), item = staticImageCases.find(value => value.id === entry.caseId);
                    const stage = item.stages.find(value => value.id === entry.stageId), state = this.states.get(entry.caseId);
                    if (action.skipReason) checkImages(reply.length === 0);
                    else {
                        const result = imageReply(reply.toString('ascii'), item, stage, state);
                        if (result.id !== null) { state.ids.push(result.id); state.firstId ??= result.id; }
                    }
                } else checkImages(reply.length === 0);
            }
            const pair = await this.capture(budget); this.lastAttempt.pair = structuredClone(pair);
            validateImageOwnership(ownership, this.frames[0]?.ownership ?? ownership, pair.screen);
            if (this.phase === 'start') {
                checkImages(pristineEditing(pair.screen) && pair.screen.rows >= 8 && pair.screen.cols >= 60);
                checkImages(pair.pixels[0].width * pair.pixels[0].height * 62 <= totalPixelLimit);
                this.baseline = pair.screen; this.bound = imageBoundScreen(pair.screen, this.runId);
                mkdirSync(join(this.output, 'image-inputs'), { mode: 0o700 });
                for (const input of imageInputs()) writeFileSync(join(this.output, input.file), input.bytes, { flag: 'wx', mode: 0o600 });
            } else checkImages(snapshotHash(pair.screen) === snapshotHash(entry.kind === 'finish' ? this.baseline : this.bound));
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
