// Parent-side observer of the cursor qualification. Every read journals one complete, coherent state (stored cells and attributes, cursor and its
// mode, margins, origin, wrap state and history count). A CPR read additionally binds the raw reply the worker received to the DA barrier that
// ordered it. A capture read brackets a controlled render with two independently bounded state reads: the render must describe the very state
// observed (cursor position, stored mode and visibility) at the requested synthetic presentation phase, and may not change it.
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { captureRequest, observerEndpoint, OwnedCellObserver, snapshotHash } from './agent-observer.mjs';
import { validatePixelCapture, defaultCursorPolicy, stablePixelContext, pngHash, totalPixelLimit } from './pixels-capture.mjs';
import { validateCursorReport } from './cursor-report.mjs';
import { cursorQualPhases } from './cursor-qual-contracts.mjs';

const fail = message => { throw new Error(message); };
const phases = Object.values(cursorQualPhases);

/** Validate the extras one read request may carry: a CPR exchange, a capture phase, both refused together. */
export function validateReadExtras(request) {
    const { cpr, capture } = request;
    if (cpr !== undefined && capture !== undefined) fail('A cursor read is either a CPR exchange or a capture.');
    if (cpr !== undefined) {
        const report = validateCursorReport(cpr);
        if (report.barrier !== request.barrier) fail('The cursor report was not ordered by this read barrier.');
        return { cpr: report };
    }
    if (capture !== undefined) {
        if (!capture || Object.keys(capture).some(key => key !== 'timeMs') || !phases.includes(capture.timeMs)) fail('Invalid cursor capture phase.');
        return { capture: { timeMs: capture.timeMs } };
    }
    return {};
}

export class CursorQualObserver {
    constructor({ output, captureRpc = captureRequest, ...options }) {
        this.output = output;
        this.captureRpc = captureRpc;
        const details = { ...options, prefix: 'curq', includeAttributes: true, includeCellDetails: true, includeCursorMode: true, includeOverline: true, includeBufferState: true };
        this.before = new OwnedCellObserver(details);
        this.after = new OwnedCellObserver(details);
        this.frames = [];
        this.captures = 0;
    }
    get restored() { return this.before.restored && this.frames.at(-1)?.kind === 'curq-finish'; }
    get paneId() { return this.before.paneId; }
    async handle(request, budget) {
        if (this.busy || this.restored) throw new Error('Cursor observation busy or complete.');
        const operation = request.kind.slice('curq-'.length);
        const extras = operation === 'read' ? validateReadExtras(request) : (request.cpr !== undefined || request.capture !== undefined ? fail('Only reads carry a report or capture.') : {});
        this.busy = true;
        try {
            const result = await this.before.handle(request.kind, budget, request.barrier);
            if (!extras.capture) {
                this.frames.push({ ...this.before.frames.at(-1), ...(extras.cpr ? { cpr: extras.cpr } : {}) });
                return result;
            }
            this.after.paneId ??= this.paneId;
            const { timeMs } = extras.capture;
            const endpoint = (this.before.endpoint ?? observerEndpoint)(this.before.appData, this.before.ownerPid);
            const capture = await this.captureRpc(endpoint, this.paneId, timeMs, budget());
            const after = await this.after.capture(budget);
            if (snapshotHash(result.screen) !== snapshotHash(after)) throw new Error('Cursor capture changed the observed screen.');
            const { bytes, details } = validatePixelCapture(capture, result.screen, timeMs);
            if (capture.width * capture.height * 24 > totalPixelLimit) throw new Error('Cursor suite decoded budget exhausted.');
            const policy = defaultCursorPolicy(result.screen, details);
            if (this.defaultCursor && policy && policy !== this.defaultCursor) fail('Invalid or changed controlled pixel capture.');
            this.defaultCursor ??= policy;
            const stable = stablePixelContext(details);
            if (this.context && stable !== this.context) fail('Invalid or changed controlled pixel capture.');
            this.context ??= stable;
            const file = `cursor-pixels/frame-${String(this.frames.length).padStart(3, '0')}.png`;
            const pixel = { file, sha256: pngHash(bytes), byteCount: bytes.length, width: capture.width, height: capture.height,
                rows: capture.rows, cols: capture.cols, mode: capture.mode, scale: capture.scale, downscaled: false,
                inlineOmitted: false, presentationTimeMs: timeMs, renderDetails: details };
            if (!this.captures++) mkdirSync(join(this.output, 'cursor-pixels'), { mode: 0o700 });
            writeFileSync(join(this.output, file), bytes, { flag: 'wx', mode: 0o600 });
            this.frames.push({ ...this.before.frames.at(-1), afterScreen: after, afterSha256: snapshotHash(after), pixel });
            return { ...result, pixel: { sha256: pixel.sha256 } };
        } finally { this.busy = false; }
    }
}
