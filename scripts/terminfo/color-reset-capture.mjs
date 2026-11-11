// Parent-side observer of the runtime-color reset qualification. Every read journals one complete, coherent state (stored cells and
// attributes, the App's resolved colors and override metadata, cursor and its mode) bracketing one controlled offscreen render (the
// whole render theme, the presentation phase and the original PNG), together with the exact query replies the worker received, each
// bound to the DA barrier that ordered it. The render must describe the very state observed and may not change it. Runtime colors are
// session-wide, so the suite starts from a pristine private theme and may only finish when the original state, theme and PNG return.
import { writeFileSync, mkdirSync, readFileSync, lstatSync } from 'node:fs';
import { join } from 'node:path';
import { colorCaptureRequest, observerEndpoint, OwnedCellObserver, snapshotHash } from './agent-observer.mjs';
import { totalPixelLimit, pngHash, defaultCursorPolicy } from './pixels-capture.mjs';
import { validateColorCapture, stableColorContext, validateQueries, pristineColorTheme } from './color-capture.mjs';
import { pristineCursorBaseline } from './cursor.mjs';
import { colorResetReadPlan } from './color-reset-contracts.mjs';

const fail = message => { throw new Error(message); };
const directory = 'crst-pixels';

/** A retained render: the original PNG bytes revalidated against the frame that carries them, refusing symlinks and changed bytes. */
export function retainedColorResetCapture(output, frame) {
    const pixel = frame?.pixel;
    if (!pixel || !new RegExp(String.raw`^${directory}/frame-\d{3}\.png$`).test(pixel.file ?? '') || !/^[a-f\d]{64}$/.test(pixel.sha256 ?? '')) fail('Invalid retained color-reset capture.');
    const path = join(output, pixel.file), entry = lstatSync(path);
    if (!entry.isFile() || entry.isSymbolicLink() || entry.size > 5 * 1024 * 1024) fail('Invalid retained color-reset capture file.');
    const bytes = readFileSync(path);
    if (bytes.length !== pixel.byteCount || pngHash(bytes) !== pixel.sha256) fail('Retained color-reset PNG bytes changed.');
    return validateColorCapture({ ...pixel, pngBase64: bytes.toString('base64'), renderDetails: pixel.renderDetails }, frame.screen, null);
}

export class ColorResetObserver {
    constructor({ output, captureRpc = colorCaptureRequest, ...options }) {
        this.output = output;
        this.captureRpc = captureRpc;
        const details = { ...options, prefix: 'crst', includeAttributes: true, includeCellDetails: true, includeCursorMode: true, includeOverline: true, includeResolvedColors: true };
        this.before = new OwnedCellObserver(details);
        this.after = new OwnedCellObserver(details);
        this.frames = [];
        this.next = 0;
    }
    get restored() { return this.before.restored && this.frames.at(-1)?.kind === 'crst-finish'; }
    get paneId() { return this.before.paneId; }

    /** The first frame must be a pristine private baseline; the finish frame must reproduce it exactly (render theme and PNG included). */
    validateRestoration(kind, screen, pixel, details) {
        if (!this.frames.length && (!pristineColorTheme(details.theme) || !pristineCursorBaseline(screen) || screen.cellDetails.some(cell => cell.overline))) {
            fail('Runtime-color reset requires a pristine private baseline.');
        }
        if (kind === 'crst-finish' && (pixel.sha256 !== this.frames[0].pixel.sha256
            || snapshotHash(details.theme) !== snapshotHash(this.frames[0].pixel.renderDetails.theme))) fail('Runtime-color baseline was not restored.');
    }

    /** The planned read this request must be (null for start, bind and finish), refusing any other case, experiment, stage or early finish. */
    plannedRead(request) {
        const operation = request.kind.slice('crst-'.length);
        const expected = operation === 'read' ? colorResetReadPlan[this.next] : null;
        if (operation === 'read' && (!expected || request.caseId !== expected.caseId || request.experimentId !== expected.experimentId || request.stageId !== expected.stageId)) {
            fail('Unexpected color-reset read.');
        }
        if (operation !== 'read' && (request.caseId !== undefined || request.queries !== undefined)) fail('Only reads carry color replies.');
        if (operation === 'finish' && this.next !== colorResetReadPlan.length) fail('Color-reset finish before the plan completed.');
        return expected;
    }

    /** One controlled render of the observed state: bracketed by two identical reads, within the suite's pixel budget and the shared render context. */
    async render(result, budget) {
        this.after.paneId ??= this.paneId;
        const endpoint = (this.before.endpoint ?? observerEndpoint)(this.before.appData, this.before.ownerPid);
        const capture = await this.captureRpc(endpoint, this.paneId, null, budget());
        const after = await this.after.capture(budget);
        if (snapshotHash(result.screen) !== snapshotHash(after)) fail('Color-reset capture changed the observed screen.');
        const { bytes, details } = validateColorCapture(capture, result.screen, null);
        if (capture.width * capture.height * (colorResetReadPlan.length + 3) > totalPixelLimit) fail('Color-reset suite decoded budget exhausted.');
        const policy = defaultCursorPolicy(result.screen, details);
        if (this.defaultCursor && policy && policy !== this.defaultCursor) fail('Changed default cursor policy.');
        this.defaultCursor ??= policy;
        const stable = stableColorContext(details);
        if (this.context && stable !== this.context) fail('Changed render context.');
        this.context ??= stable;
        return { capture, bytes, details, after };
    }

    async handle(request, budget) {
        if (this.busy || this.restored) fail('Color-reset observation busy or complete.');
        const expected = this.plannedRead(request);
        const queries = validateQueries(request.queries ?? [], expected?.queries ?? []);
        this.busy = true;
        try {
            const result = await this.before.handle(request.kind, budget, request.barrier);
            const { capture, bytes, details, after } = await this.render(result, budget);
            const file = `${directory}/frame-${String(this.frames.length).padStart(3, '0')}.png`;
            const pixel = { file, sha256: pngHash(bytes), byteCount: bytes.length, width: capture.width, height: capture.height,
                rows: capture.rows, cols: capture.cols, mode: capture.mode, scale: capture.scale, downscaled: false,
                inlineOmitted: false, presentationTimeMs: 0, renderDetails: details };
            this.validateRestoration(request.kind, result.screen, pixel, details);
            if (!this.frames.length) mkdirSync(join(this.output, directory), { mode: 0o700 });
            writeFileSync(join(this.output, file), bytes, { flag: 'wx', mode: 0o600 });
            this.frames.push({ ...this.before.frames.at(-1), afterScreen: after, afterSha256: snapshotHash(after),
                caseId: expected?.caseId ?? null, experimentId: expected?.experimentId ?? null, stageId: expected?.stageId ?? null, queries, pixel });
            if (request.kind === 'crst-read') this.next++;
            return result;
        } finally { this.busy = false; }
    }
}
