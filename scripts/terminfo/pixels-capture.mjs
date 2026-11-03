import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync, mkdirSync, lstatSync } from 'node:fs';
import { join } from 'node:path';
import { captureRequest, observerEndpoint, OwnedCellObserver, snapshotHash } from './agent-observer.mjs';
import { decodeCapturePng, limits } from './pixels-png.mjs';
import { pixelPlan } from './pixels-cases.mjs';

export const totalPixelLimit = 64_000_000;
export const pngHash = bytes => createHash('sha256').update(bytes).digest('hex');
const fail = () => { throw new Error('Invalid or changed controlled pixel capture.'); };
const integer = (value, min, max) => Number.isSafeInteger(value) && value >= min && value <= max;
const finite = (value, min, max) => Number.isFinite(value) && value >= min && value <= max;
const sha = value => typeof value === 'string' && /^[a-f\d]{64}$/.test(value);
const name = value => typeof value === 'string' && value.length > 0 && value.length <= 512;

/** Validate the actual draw context; missing font fingerprints are not pixel qualifications. */
function validateDetails(details, screen, image, timeMs) {
    if (!details || details.schemaVersion !== 1 || !['macos', 'linux', 'windows'].includes(details.platform)
        || !/^(arm64|x64|x86|arm|wasm|s390x|loongarch64|ppc64le|riscv64)$/.test(details.architecture ?? '')
        || details.enableLigatures !== false || details.minimumContrast !== 1 || typeof details.enableComplexShaping !== 'boolean') fail();
    const { font, grid, theme, presentation: phase } = details;
    if (!font || !name(font.configuredFamily) || !name(font.primaryFamily) || !sha(font.primarySha256)
        || !integer(font.collectionIndex, 0, 65535) || !integer(font.weight, 1, 1000) || !integer(font.width, 1, 9)
        || !name(font.slant) || !finite(font.sizeDip, 4, 96) || !Array.isArray(font.fallbackFamilies)
        || font.fallbackFamilies.length > 64 || !font.fallbackFamilies.every(name)) fail();
    if (!grid || image.width !== Math.ceil(screen.cols * grid.cellWidthDip) || image.height !== Math.ceil(screen.rows * grid.cellHeightDip)
        || grid.renderWidthPx !== image.width || grid.renderHeightPx !== image.height
        || !integer(grid.originXPx, 0, image.width) || !integer(grid.originYPx, 0, image.height)
        || !integer(grid.cellWidthPx, 1, 256) || !integer(grid.cellHeightPx, 1, 512)
        || !finite(grid.cellWidthDip, 1, 256) || !finite(grid.cellHeightDip, 1, 512)
        || !finite(grid.baselineDip, 0, grid.cellHeightDip)
        || !['baselineOffsetPx', 'underlineOffsetPx', 'strikeOffsetPx'].every(key => integer(grid[key], 0, grid.cellHeightPx))) fail();
    if (!theme || !['foregroundArgb', 'backgroundArgb', 'cursorArgb', 'cursorTextArgb'].every(key => integer(theme[key], 0xff000000, 0xffffffff))
        || !Array.isArray(theme.ansiPaletteArgb) || theme.ansiPaletteArgb.length !== 256
        || !theme.ansiPaletteArgb.every(color => integer(color, 0xff000000, 0xffffffff))
        || theme.foregroundOverride !== false || theme.backgroundOverride !== false) fail();
    const visiblePhase = Math.floor(timeMs / 530) % 2 === 0;
    const mode = screen.cursorMode;
    if (!phase || phase.timeMs !== timeMs || phase.halfPeriodMs !== 530 || phase.textBlinkVisible !== visiblePhase
        || !integer(phase.cursorStyle, 0, 2) || typeof phase.cursorBlinkEnabled !== 'boolean'
        || phase.cursorRow !== screen.cursorRow || phase.cursorCol !== screen.cursorCol
        || phase.cursorVisible !== (screen.cursorVisible && (!phase.cursorBlinkEnabled || visiblePhase))
        || (!mode.isDefault && (phase.cursorStyle !== mode.style || phase.cursorBlinkEnabled !== mode.blink))) fail();
}

/** Refuse resamples, clock substitutions, malformed inline bytes, dimensions and ambiguous provenance. */
export function validatePixelCapture(result, screen, timeMs) {
    if (result?.mode !== 'render' || result.scale !== 1 || result.downscaled !== false || result.inlineOmitted !== false
        || result.presentationTimeMs !== timeMs || result.rows !== screen.rows || result.cols !== screen.cols
        || !integer(result.byteCount, 1, limits.bytes) || typeof result.pngBase64 !== 'string'
        || result.pngBase64.length > Math.ceil(limits.bytes / 3) * 4) fail();
    const bytes = Buffer.from(result.pngBase64, 'base64');
    if (bytes.length !== result.byteCount || bytes.toString('base64') !== result.pngBase64) fail();
    const image = decodeCapturePng(bytes);
    if (result.width !== image.width || result.height !== image.height) fail();
    validateDetails(result.renderDetails, screen, image, timeMs);
    return { bytes, image, details: structuredClone(result.renderDetails) };
}

export function stablePixelContext(details) {
    const { presentation, ...stable } = details;
    return snapshotHash(stable);
}

/** Read a retained image without accepting path traversal, symlinks or changed bytes. */
export function retainedPixel(output, frame) {
    const pixel = frame?.pixel;
    if (!pixel || !/^pixels\/frame-\d{3}\.png$/.test(pixel.file ?? '') || !sha(pixel.sha256)) fail();
    const path = join(output, pixel.file), entry = lstatSync(path);
    if (!entry.isFile() || entry.isSymbolicLink() || entry.size > limits.bytes) fail();
    const bytes = readFileSync(path);
    if (bytes.length !== pixel.byteCount || pngHash(bytes) !== pixel.sha256) fail();
    const result = { ...pixel, pngBase64: bytes.toString('base64'), renderDetails: pixel.renderDetails };
    return validatePixelCapture(result, frame.screen, pixel.presentationTimeMs).image;
}

/** Each logical capture brackets a fixed render with two independently bounded complete state reads. */
export class OwnedPixelObserver {
    constructor({ output, captureRpc = captureRequest, ...options }) {
        this.output = output;
        this.captureRpc = captureRpc;
        this.before = new OwnedCellObserver({ ...options, prefix: 'pixel', includeCellDetails: true, includeCursorMode: true, includeOverline: true });
        this.after = new OwnedCellObserver({ ...options, prefix: 'pixel', includeCellDetails: true, includeCursorMode: true, includeOverline: true });
        this.frames = [];
        this.next = 0;
    }
    get restored() { return this.before.restored && this.frames.at(-1)?.kind === 'pixel-finish'; }
    get paneId() { return this.before.paneId; }
    async handle(request, budget) {
        if (this.busy || this.restored) throw new Error('Pixel observation busy or complete.');
        const expected = request.kind === 'pixel-read' ? pixelPlan[this.next] : { timeMs: 0 };
        if (!expected || request.timeMs !== expected.timeMs || request.sceneId !== expected.sceneId
            || (request.kind === 'pixel-finish' && this.next !== pixelPlan.length)) fail();
        this.busy = true;
        try {
            const result = await this.before.handle(request.kind, budget, request.barrier);
            this.after.paneId ??= this.paneId;
            const endpoint = (this.before.endpoint ?? observerEndpoint)(this.before.appData, this.before.ownerPid);
            const capture = await this.captureRpc(endpoint, this.paneId, request.timeMs, budget());
            const after = await this.after.capture(budget);
            if (snapshotHash(result.screen) !== snapshotHash(after)) throw new Error('Pixel capture changed the observed screen.');
            const { bytes, details } = validatePixelCapture(capture, result.screen, request.timeMs);
            if (capture.width * capture.height * (pixelPlan.length + 3) > totalPixelLimit) throw new Error('Pixel suite decoded budget exhausted.');
            const stable = stablePixelContext(details);
            if (this.context && stable !== this.context) fail();
            this.context ??= stable;
            const file = `pixels/frame-${String(this.frames.length).padStart(3, '0')}.png`;
            const pixel = { file, sha256: pngHash(bytes), byteCount: bytes.length, width: capture.width, height: capture.height,
                rows: capture.rows, cols: capture.cols, mode: capture.mode, scale: capture.scale, downscaled: false,
                inlineOmitted: false, presentationTimeMs: request.timeMs, renderDetails: details };
            if (request.kind === 'pixel-finish' && pixel.sha256 !== this.frames[0].pixel.sha256) throw new Error('Pixel baseline was not restored.');
            if (!this.frames.length) mkdirSync(join(this.output, 'pixels'), { mode: 0o700 });
            writeFileSync(join(this.output, file), bytes, { flag: 'wx', mode: 0o600 });
            this.frames.push({ ...this.before.frames.at(-1), afterScreen: after, afterSha256: snapshotHash(after), sceneId: request.sceneId ?? null, pixel });
            if (request.kind === 'pixel-read') this.next++;
            return result;
        } finally { this.busy = false; }
    }
}
