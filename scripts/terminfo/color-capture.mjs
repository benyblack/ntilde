import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { colorCaptureRequest, observerEndpoint, OwnedCellObserver, snapshotHash } from './agent-observer.mjs';
import { validateRenderedCapture, totalPixelLimit, pngHash, defaultCursorPolicy } from './pixels-capture.mjs';
import { colorPlan } from './color-cases.mjs';
import { parseColorReply, querySpec } from './color-query.mjs';
import { pristineCursorBaseline } from './cursor.mjs';
const fail = () => { throw new Error('Invalid owned color capture or query binding.'); };
const uint = value => Number.isSafeInteger(value) && value >= 0 && value <= 0xffffffff;
const nullableColor = value => value === null || uint(value);
export function pristineColorTheme(theme) {
    return ['foregroundOverride', 'backgroundOverride', 'cursorOverride', 'selectionBackgroundOverride', 'selectionForegroundOverride']
        .every(key => theme[key] === false) && theme.paletteOverrideIndices.length === 0;
}
export function validateColorCapture(result, screen, selection) {
    const value = validateRenderedCapture(result, screen, 0), theme = value.details.theme;
    if (!['cursorOverride', 'selectionBackgroundOverride', 'selectionForegroundOverride'].every(key => typeof theme[key] === 'boolean')
        || !['selectionBackgroundArgb', 'selectionForegroundArgb', 'effectiveSelectionForegroundArgb'].every(key => Object.hasOwn(theme, key) && nullableColor(theme[key]))
        || !uint(theme.effectiveSelectionBackgroundArgb) || !Array.isArray(theme.paletteOverrideIndices)
        || !theme.paletteOverrideIndices.every((index, i, all) => Number.isSafeInteger(index) && index >= 0 && index < 256 && (!i || index > all[i - 1]))
        || snapshotHash(result.syntheticSelection ?? null) !== snapshotHash(selection)
        || snapshotHash(value.details.syntheticSelection ?? null) !== snapshotHash(selection)) fail();
    return value;
}
export function stableColorContext(details) {
    const { presentation, theme, syntheticSelection, ...stable } = details;
    return snapshotHash(stable);
}
export function validateQueries(queries, expected) {
    if (!Array.isArray(queries) || queries.length !== expected.length) fail();
    return queries.map((item, index) => {
        if (snapshotHash(querySpec(item.spec)) !== snapshotHash(expected[index]) || !/^\x1b\[\?[\d;]+c$/.test(item.barrier ?? '') || item.barrier.length > 128) fail();
        const reply = item.reply === null ? null : parseColorReply(item.reply?.raw, expected[index]);
        if (snapshotHash(reply) !== snapshotHash(item.reply)) fail();
        return { spec: querySpec(item.spec), barrier: item.barrier, reply };
    });
}

export class OwnedColorObserver {
    constructor({ output, captureRpc = colorCaptureRequest, ...options }) {
        this.output = output;
        this.captureRpc = captureRpc;
        this.before = new OwnedCellObserver({ ...options, prefix: 'color', includeCellDetails: true, includeCursorMode: true, includeOverline: true });
        this.after = new OwnedCellObserver({ ...options, prefix: 'color', includeCellDetails: true, includeCursorMode: true, includeOverline: true });
        this.frames = [];
        this.next = 0;
    }
    get restored() { return this.before.restored && this.frames.at(-1)?.kind === 'color-finish'; }
    get paneId() { return this.before.paneId; }
    async handle(request, budget) {
        if (this.busy || this.restored) throw new Error('Pixel observation busy or complete.');
        const expected = request.kind === 'color-read' ? colorPlan[this.next] : { selection: null, queries: [] };
        if (!expected || request.caseId !== expected.caseId || request.stageId !== expected.stageId
            || (request.kind === 'color-finish' && this.next !== colorPlan.length)) fail();
        const queries = validateQueries(request.queries ?? [], expected.queries);
        this.busy = true;
        try {
            const result = await this.before.handle(request.kind, budget, request.barrier);
            this.after.paneId ??= this.paneId;
            const endpoint = (this.before.endpoint ?? observerEndpoint)(this.before.appData, this.before.ownerPid);
            const capture = await this.captureRpc(endpoint, this.paneId, expected.selection, budget());
            const after = await this.after.capture(budget);
            if (snapshotHash(result.screen) !== snapshotHash(after)) throw new Error('Pixel capture changed the observed screen.');
            const { bytes, details } = validateColorCapture(capture, result.screen, expected.selection);
            if (capture.width * capture.height * (colorPlan.length + 3) > totalPixelLimit) throw new Error('Pixel suite decoded budget exhausted.');
            const policy = defaultCursorPolicy(result.screen, details);
            if (this.defaultCursor && policy && policy !== this.defaultCursor) fail();
            this.defaultCursor ??= policy;
            const stable = stableColorContext(details);
            if (this.context && stable !== this.context) fail();
            this.context ??= stable;
            const file = `color-pixels/frame-${String(this.frames.length).padStart(3, '0')}.png`;
            const pixel = { file, sha256: pngHash(bytes), byteCount: bytes.length, width: capture.width, height: capture.height,
                rows: capture.rows, cols: capture.cols, mode: capture.mode, scale: capture.scale, downscaled: false,
                inlineOmitted: false, presentationTimeMs: 0, ...(expected.selection ? { syntheticSelection: structuredClone(expected.selection) } : {}), renderDetails: details };
            if (!this.frames.length && (!pristineColorTheme(details.theme) || !pristineCursorBaseline(result.screen) || result.screen.cellDetails.some(cell => cell.overline))) fail();
            if (request.kind === 'color-finish' && (pixel.sha256 !== this.frames[0].pixel.sha256 || snapshotHash(details.theme) !== snapshotHash(this.frames[0].pixel.renderDetails.theme))) throw new Error('Pixel baseline was not restored.');
            if (!this.frames.length) mkdirSync(join(this.output, 'color-pixels'), { mode: 0o700 });
            writeFileSync(join(this.output, file), bytes, { flag: 'wx', mode: 0o600 });
            this.frames.push({ ...this.before.frames.at(-1), afterScreen: after, afterSha256: snapshotHash(after), caseId: request.caseId ?? null, stageId: request.stageId ?? null, queries, pixel });
            if (request.kind === 'color-read') this.next++;
            return result;
        } finally { this.busy = false; }
    }
}
