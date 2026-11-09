// Synthetic, fully valid cursor grading evidence for portable tests. A tiny byte-interpreting terminal executes the exact bytes of every plan step
// (so the states are not copied from the contract's own expectations), with behavior switches that build the negative evidence the tests need:
// relative/absolute coordinate confusion, wrong margins, ignored DECOM, a missing reverse-wrap, a stored-only hide, a mis-shaped cursor and more.
// Every frame, run-log entry and pinned result passes through the same production validators used for real measurements.
import { createHash } from 'node:crypto';
import { crc32, deflateSync } from 'node:zlib';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { snapshotHash } from '../agent-observer.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { makeCursorQualContracts, cursorQualNoncePrefix } from '../cursor-qual-contracts.mjs';
import { replayCursorQualPinned, cursorQualDerivedFrom, pinnedCallbackDigests } from '../cursor-qual-grading.mjs';
import { cursorGeometryRepairId, cursorQualAdapterId as adapterId } from '../cursor-qual-grading-collector.mjs';

export const cursorRows = 24;
export const cursorCols = 60;
const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';
export const cellWidth = 9, cellHeight = 19;
export const hostDefaultMode = Object.freeze({ style: 2, blink: true });
export const syntheticBackground = 0xff000000, syntheticCursorColor = 0xffffffff;

/** The default behavior: a terminal that implements every contracted operation correctly. */
export const correctBehavior = Object.freeze({ reverseWrap: true, cprRelativeUnderOrigin: true, cupFollowsOrigin: true, marginShift: 0, clampAtRegionBottom: true,
    hideStored: true, hidePaints: true, shapeOfFive: 'bar', paintsBar: true, blinkAware: true, rendersHidden: false,
    zeroQIsExplicit: false, reverseWrapIgnoresMode: false, ignoreOriginReset: false, noCpr: false });

/** A byte-interpreting terminal with just the operations the cursor fixtures use. */
export class SimTerminal {
    constructor(behavior = correctBehavior, rows = cursorRows, cols = cursorCols) {
        this.behavior = { ...correctBehavior, ...behavior };
        this.rows = rows; this.cols = cols;
        this.alt = false; this.saved = null;
        this.blank();
        this.row = 0; this.col = 0; this.pending = false; this.top = 0; this.bottom = rows - 1; this.origin = false; this.autoWrap = true; this.reverseWrap = false;
        this.visible = true; this.mode = { ...hostDefaultMode, isDefault: true };
    }
    blank() { this.cells = Array.from({ length: this.rows }, () => Array(this.cols).fill(' ')); }
    /** The CUP/CUU/CUD row limits for the current origin state. */
    limits() { return this.origin ? [this.top, this.bottom] : [0, this.rows - 1]; }
    feed(bytes) {
        for (let index = 0; index < bytes.length;) {
            const char = bytes[index];
            if (char === '\x1b' && bytes[index + 1] === '[') {
                let end = index + 2;
                while (end < bytes.length && !/[@-~]/.test(bytes[end])) end++;
                this.csi(bytes.slice(index + 2, end), bytes[end]);
                index = end + 1;
            } else if (char === '\x1b') index += 2;
            else { this.text(char); index++; }
        }
    }
    text(char) {
        if (char === '\b') {
            if (this.col > 0) { this.col--; this.pending = false; return; }
            this.pending = false;
            if (this.behavior.reverseWrap && (this.reverseWrap || this.behavior.reverseWrapIgnoresMode) && this.autoWrap && this.row > this.limits()[0]) { this.row--; this.col = this.cols - 1; }
            return;
        }
        if (this.pending && this.autoWrap) { this.col = 0; this.row = Math.min(this.row + 1, this.rows - 1); this.pending = false; }
        this.cells[this.row][this.col] = char;
        if (this.col === this.cols - 1) this.pending = true; else this.col++;
    }
    moveTo(row, col) {
        const [low, high] = this.limits();
        this.row = Math.min(Math.max(low + row, low), this.behavior.clampAtRegionBottom ? high : this.rows - 1);
        this.col = Math.min(Math.max(col, 0), this.cols - 1);
        this.pending = false;
    }
    csi(parameters, final) {
        const priv = parameters.startsWith('?'), spaced = parameters.endsWith(' ');
        const numbers = parameters.replace(/^\?/, '').replace(/ $/, '').split(';').map(value => (value === '' ? NaN : Number(value)));
        const first = Number.isNaN(numbers[0]) ? undefined : numbers[0];
        if (spaced && final === 'q') return this.cursorStyle(first ?? 0);
        if (priv && (final === 'h' || final === 'l')) {
            const set = final === 'h';
            for (const mode of numbers) this.privateMode(mode, set);
            return undefined;
        }
        switch (final) {
            case 'r': {
                const top = (first ?? 1) - 1, bottom = (Number.isNaN(numbers[1]) || numbers[1] === undefined ? this.rows : numbers[1]) - 1 + this.behavior.marginShift;
                this.top = top; this.bottom = bottom; if (first === undefined) { this.top = 0; this.bottom = this.rows - 1; }
                this.moveTo(0, 0); this.row = this.origin ? this.top : 0;
                return undefined;
            }
            case 'H': case 'f': {
                const row = (first ?? 1) - 1, col = (Number.isNaN(numbers[1]) || numbers[1] === undefined ? 1 : numbers[1]) - 1;
                if (this.behavior.cupFollowsOrigin) this.moveTo(row, col);
                else { this.row = Math.min(Math.max(row, 0), this.rows - 1); this.col = Math.min(col, this.cols - 1); this.pending = false; }
                return undefined;
            }
            case 'A': this.row = Math.max(this.row - (first ?? 1), this.row >= this.top ? this.top : 0); this.pending = false; return undefined;
            case 'B': this.row = Math.min(this.row + (first ?? 1), this.row <= this.bottom ? this.bottom : this.rows - 1); this.pending = false; return undefined;
            case 'J': if (first === 2) this.blank(); return undefined;
            default: return undefined;
        }
    }
    cursorStyle(code) {
        if (code === 0) { this.mode = { ...hostDefaultMode, isDefault: !this.behavior.zeroQIsExplicit }; return; }
        if (code > 6) return;
        const effective = code === 5 && this.behavior.shapeOfFive === 'block' ? 1 : code;
        this.mode = { style: effective >= 5 ? 1 : effective >= 3 ? 2 : 0, blink: effective % 2 === 1, isDefault: false };
    }
    privateMode(mode, set) {
        if (mode === 6 && !set && this.behavior.ignoreOriginReset) return;
        if (mode === 6) { this.origin = set; this.row = set ? this.top : 0; this.col = 0; this.pending = false; }
        else if (mode === 7) this.autoWrap = set;
        else if (mode === 45) this.reverseWrap = set;
        else if (mode === 25 && this.behavior.hideStored) this.visible = set;
        else if (mode === 25) this.visible = true;
        else if (mode === 1049) {
            if (set && !this.alt) { this.saved = { cells: this.cells, row: this.row, col: this.col }; this.alt = true; this.blank(); }
            else if (!set && this.alt) { this.alt = false; this.cells = this.saved.cells; this.row = this.saved.row; this.col = this.saved.col; }
        }
    }
    /** The CPR a terminal answers (1-based), region-relative under DECOM unless the behavior is the confused one. */
    report() {
        const relative = this.origin && this.behavior.cprRelativeUnderOrigin ? this.top : 0;
        return [Math.max(1, this.row - relative + 1), this.col + 1];
    }
    /** One complete valid observation screen of the current state. */
    screen() {
        const total = this.rows * this.cols;
        return { rows: this.rows, cols: this.cols, lines: this.cells.map(line => line.join('').trimEnd()), cursorRow: this.row, cursorCol: this.col, cursorVisible: this.visible,
            attributeRuns: [{ count: total, flags: 0x300, fg: -1, bg: -1 }],
            cellDetails: [{ overline: false, count: total, flags: 0x300, fg: -1, bg: -1, foregroundArgb: null, backgroundArgb: null, underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null }],
            cursorMode: { ...this.mode }, bufferState: { scrollTop: this.top, scrollBottom: this.bottom, originMode: this.origin, autoWrap: this.autoWrap, insertMode: false,
                pendingWrap: this.pending, alternateScreen: this.alt, scrollbackLines: 0 },
            history: { startLine: 0, totalLines: 0, lines: [] } };
    }
    /** A synthetic render of the current state at a presentation phase: the cursor cell is painted by shape and phase. */
    render(timeMs) {
        const width = this.cols * cellWidth, height = this.rows * cellHeight, pixels = Buffer.alloc(width * height * 4);
        const put = (offset, argb) => { pixels[offset] = (argb >>> 16) & 255; pixels[offset + 1] = (argb >>> 8) & 255; pixels[offset + 2] = argb & 255; pixels[offset + 3] = argb >>> 24; };
        for (let offset = 0; offset < pixels.length; offset += 4) put(offset, syntheticBackground);
        const paint = (x, y, color) => put((y * width + x) * 4, color);
        for (const [row, line] of this.cells.entries()) {
            for (const [col, char] of line.entries()) {
                if (char === ' ') continue;
                for (let y = 5; y < 14; y++) paint(col * cellWidth + 3, row * cellHeight + y, 0xff909090);
            }
        }
        const visiblePhase = Math.floor(timeMs / 530) % 2 === 0;
        const shown = (this.behavior.hidePaints ? this.visible : true) && (!this.mode.blink || visiblePhase || !this.behavior.blinkAware);
        if (shown || this.behavior.rendersHidden) {
            const x0 = this.col * cellWidth, y0 = this.row * cellHeight, style = this.mode.style;
            const blockLike = style === 0 || (style === 1 && !this.behavior.paintsBar);
            for (let y = 0; y < cellHeight; y++) {
                for (let x = 0; x < cellWidth; x++) {
                    if (blockLike || (style === 2 && y >= cellHeight - 3 && y < cellHeight - 1) || (style === 1 && x === 0)) paint(x0 + x, y0 + y, syntheticCursorColor);
                }
            }
        }
        return { width, height, pixels };
    }
}

const frame = (index, kind, screen, barrier, extra = {}) => ({ index, kind, paneId, barrier, screen, sha256: snapshotHash(screen), ...extra });
const sha = bytes => createHash('sha256').update(bytes).digest('hex');

function chunk(type, data) {
    const result = Buffer.alloc(data.length + 12);
    result.writeUInt32BE(data.length); result.write(type, 4); data.copy(result, 8);
    result.writeUInt32BE(crc32(result.subarray(4, -4)), result.length - 4);
    return result;
}
/** An 8-bit RGB, non-interlaced PNG of an RGBA pixel buffer. */
export function encodePng({ width, height, pixels }) {
    const header = Buffer.alloc(13); header.writeUInt32BE(width); header.writeUInt32BE(height, 4); header[8] = 8; header[9] = 2;
    const raw = Buffer.alloc(height * (width * 3 + 1));
    for (let y = 0; y < height; y++) {
        for (let x = 0; x < width; x++) pixels.copy(raw, y * (width * 3 + 1) + 1 + x * 3, (y * width + x) * 4, (y * width + x) * 4 + 3);
    }
    return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', header), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}
/** The render details a valid capture of one screen at one synthetic phase carries (validated by the production capture validator). */
export function syntheticRenderDetails(screen, timeMs, width, height) {
    const visiblePhase = Math.floor(timeMs / 530) % 2 === 0, blink = screen.cursorMode.blink;
    return { schemaVersion: 1, platform: 'macos', architecture: 'arm64', enableLigatures: false, enableComplexShaping: false, minimumContrast: 1,
        font: { configuredFamily: 'fixture', primaryFamily: 'fixture', primarySha256: 'a'.repeat(64), collectionIndex: 0, weight: 400, width: 5, slant: 'Upright', fallbackFamilies: [], sizeDip: 14 },
        grid: { renderWidthPx: width, renderHeightPx: height, cellWidthDip: cellWidth, cellHeightDip: cellHeight, baselineDip: 14, originXPx: 0, originYPx: 0,
            cellWidthPx: cellWidth, cellHeightPx: cellHeight, baselineOffsetPx: 15, underlineOffsetPx: 16, strikeOffsetPx: 10 },
        theme: { foregroundArgb: 0xffffffff, backgroundArgb: syntheticBackground, cursorArgb: syntheticCursorColor, cursorTextArgb: 0xff000000,
            ansiPaletteArgb: Array.from({ length: 256 }, (_, i) => 0xff000000 + i * 65536), foregroundOverride: false, backgroundOverride: false },
        presentation: { timeMs, halfPeriodMs: 530, textBlinkVisible: visiblePhase, cursorVisible: screen.cursorVisible && (!blink || visiblePhase), cursorBlinkEnabled: blink,
            cursorStyle: screen.cursorMode.style, cursorRow: screen.cursorRow, cursorCol: screen.cursorCol } };
}

/** The synthetic contracts at the synthetic geometry. */
export const syntheticCursorContracts = () => makeCursorQualContracts(cursorRows, cursorCols);

/** A complete valid synthetic run: frames, worker run log, adapter facts and the decoded captures keyed by frame. `behavior` builds negative evidence. */
export function syntheticCursorEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), behavior = correctBehavior) {
    const contracts = syntheticCursorContracts();
    const nonce = cursorQualNoncePrefix + runId;
    const sim = new SimTerminal(behavior);
    const frames = [frame(0, 'curq-start', sim.screen(), null)];
    const images = new Map(), pngs = new Map();
    sim.feed('\x1b[?1049h'); sim.feed('\x1b[0m\x1b[?5l\x1b[?6l\x1b[?7h\x1b[?45l\x1b[r\x1b[4l\x1b[2J\x1b[H\x1b[?25h\x1b[0 q'); sim.feed(nonce);
    frames.push(frame(1, 'curq-bind', sim.screen(), deviceAttribute));
    const cases = contracts.map(contract => {
        const feeds = planFeeds(contract), barriers = [], reads = [], screens = [];
        let last, readIndex = 0;
        const planned = contract.groups.flatMap(group => group.reads);
        for (const step of contract.plan) {
            if (step.feed) { sim.feed(step.feed); last = deviceAttribute; barriers.push(last); continue; }
            const wanted = planned[readIndex++], screen = sim.screen();
            const read = { kind: step.read, cursor: [screen.cursorRow, screen.cursorCol], barrier: last };
            const extra = {};
            if (step.read === 'cpr') {
                const [row, col] = sim.report(), raw = `\x1b[${row};${col}R`;
                read.cpr = { barrier: deviceAttribute, reply: sim.behavior.noCpr ? null : { raw, row, col } };
                extra.cpr = read.cpr;
            } else if (step.read === 'capture') {
                const image = sim.render(wanted.timeMs), png = encodePng(image), digest = sha(png);
                const renderDetails = syntheticRenderDetails(screen, wanted.timeMs, image.width, image.height);
                read.timeMs = wanted.timeMs; read.pixelSha256 = digest;
                extra.pixel = { file: `cursor-pixels/frame-${String(frames.length).padStart(3, '0')}.png`, sha256: digest, byteCount: png.length, width: image.width, height: image.height,
                    rows: cursorRows, cols: cursorCols, mode: 'render', scale: 1, downscaled: false, inlineOmitted: false, presentationTimeMs: wanted.timeMs, renderDetails };
                images.set(frames.length, { image, details: renderDetails });
                pngs.set(extra.pixel.file, png);
            }
            reads.push(read);
            screens.push(screen);
            frames.push(frame(frames.length, 'curq-read', screen, last, extra));
        }
        const replayed = replayCursorQualPinned(contract, screens, reads);
        const geometry = { rows: cursorRows, cols: cursorCols };
        const repaired = contract.groups.some(group => group.kind === 'pinned-app-repaired');
        const pinned = { termless: { probeId: contract.caseId, ...replayed.termless }, app: { probeId: contract.caseId, ...replayed.app, repaired, geometry } };
        if (repaired) {
            const digests = pinnedCallbackDigests[contract.caseId];
            pinned.original = { probeId: contract.caseId, threw: { name: 'UndeclaredTerminalGeometry', message: `App callback ${contract.caseId} accessed undeclared or unavailable terminal geometry` },
                writes: contract.caseId === 'cursor.reverse-wrap' ? [] : [...contract.groups.find(group => group.kind === 'pinned-app-repaired').pinnedFeeds], geometry };
            pinned.repair = { derivationId: cursorGeometryRepairId, probeId: contract.caseId, originalKeys: ['id', 'termless', 'term'], repairedKeys: ['id', 'termless', 'term', 'termNeedsGeometry'],
                originalTermNeedsGeometry: null, repairedTermNeedsGeometry: true, sameTermless: true, sameTerm: true, termlessSha256: digests.termlessSha256, termSha256: digests.termSha256,
                metadataChange: 'termNeedsGeometry: true added to the cursor.reverse-wrap probe record; both callbacks are the original function objects' };
        }
        return { caseId: contract.caseId, feeds, barriers, reads, pinned };
    });
    sim.feed('\x1b[0m\x1b[?5l\x1b[?6l\x1b[?7h\x1b[?45l\x1b[r\x1b[4l\x1b[2J\x1b[H\x1b[?25h\x1b[0 q'); sim.feed('\x1b[?1049l');
    frames.push(frame(frames.length, 'curq-finish', sim.screen(), deviceAttribute));
    const run = { schemaVersion: 1, adapterId, probeIds: contracts.map(contract => contract.caseId), geometryRepairId: cursorGeometryRepairId, cases };
    const adapter = { id: adapterId, derivedFromAdapterId: cursorQualDerivedFrom, bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, images, pngs, imageOf: frameValue => images.get(frameValue.index) };
}

/** Write the retained PNGs of one synthetic run under `output/cursor-pixels`, as the real observer does. */
export function writeCursorPngs(evidence, output) {
    mkdirSync(join(output, 'cursor-pixels'), { recursive: true });
    for (const [file, bytes] of evidence.pngs) writeFileSync(join(output, file), bytes);
}

/** A deep copy of the mutable parts of a run (journal, run log, adapter), sharing the immutable decoded captures. */
export const cloneEvidence = evidence => ({ ...evidence, frames: structuredClone(evidence.frames), run: structuredClone(evidence.run), adapter: structuredClone(evidence.adapter) });
