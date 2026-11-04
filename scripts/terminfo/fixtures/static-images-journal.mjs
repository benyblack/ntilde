// Synthetic protocols/state/PNGs only. These are never runtime evidence.
import { crc32, deflateSync } from 'node:zlib';
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { initialInputScreen, inputPaneId, inputRunId, inputCapture, inputActionProof } from './input-journal.mjs';
import { pixelCapture } from './pixel-capture.mjs';
import { staticImageCases } from '../static-images-cases.mjs';
import { staticImageInput } from '../static-images-inputs.mjs';
import { imagePlan, imageInputs, imageBoundScreen, imageAction, imageReply, imageModeReplyHex, imageBackgroundReply } from '../static-images.mjs';
import { snapshotHash } from '../agent-observer.mjs';
import { pngHash } from '../pixels-capture.mjs';
import { defaultInputModes } from '../input-state.mjs';
const chunk = (type, data) => { const b = Buffer.alloc(data.length + 12); b.writeUInt32BE(data.length); b.write(type, 4); data.copy(b, 8); b.writeUInt32BE(crc32(b.subarray(4, -4)), b.length - 4); return b; };
function paintSource(rgba, width, spec, stage) {
        if (!spec) throw new Error('Synthetic image requires an explicit spec.');
        const size = { native: [16, 16], stretch: [32, 36], aspect: [32, 32] }[spec.geometry];
        const sourceId = stage.appearance === 'reversed' ? 'reversed' : spec.sourceId;
        const source = staticImageInput(sourceId, 'rgba').bytes;
        for (let y = 0; y < size[1]; y++) for (let x = 0; x < size[0]; x++) {
            const offset = (Math.floor(y * 16 / size[1]) * 16 + Math.floor(x * 16 / size[0])) * 4;
            if (source[offset + 3]) rgba.set(source.subarray(offset, offset + 4), ((72 + y) * width + 24 + x) * 4);
        }
}
export function syntheticImageCapture(screen, spec = null, stage = null, { blank = false, bleed = false } = {}) {
    const capture = pixelCapture(screen), width = screen.cols * 8, height = screen.rows * 18;
    const rgba = Buffer.alloc(width * height * 4), background = screen.bufferState.alternateScreen ? 16 : 0;
    for (let i = 0; i < rgba.length; i += 4) rgba.set([background, background, background, 255], i);
    if (!blank && stage && stage.appearance !== 'absent') paintSource(rgba, width, spec, stage);
    if (bleed) rgba.set([255, 0, 0, 255], 0);
    const raw = Buffer.alloc(height * (width * 4 + 1));
    for (let row = 0; row < height; row++) rgba.copy(raw, row * (width * 4 + 1) + 1, row * width * 4, (row + 1) * width * 4);
    const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(width); ihdr.writeUInt32BE(height, 4); ihdr[8] = 8; ihdr[9] = 6;
    const bytes = Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
    Object.assign(capture, { width, height, byteCount: bytes.length, pngBase64: bytes.toString('base64') });
    Object.assign(capture.renderDetails.theme, { backgroundOverride: screen.bufferState.alternateScreen,
        backgroundArgb: screen.bufferState.alternateScreen ? 0xff101010 : 0xff000000,
        cursorTextArgb: screen.bufferState.alternateScreen ? 0xff101010 : 0xff000000,
        cursorOverride: false, selectionBackgroundOverride: false, selectionForegroundOverride: false,
        selectionBackgroundArgb: null, selectionForegroundArgb: null, effectiveSelectionBackgroundArgb: 0xff123456,
        effectiveSelectionForegroundArgb: null, paletteOverrideIndices: [] });
    Object.assign(capture.renderDetails.grid, { renderWidthPx: width, renderHeightPx: height, cellWidthDip: 8, cellHeightDip: 18,
        cellWidthPx: 8, cellHeightPx: 18, baselineDip: 14, baselineOffsetPx: 14, underlineOffsetPx: 16, strikeOffsetPx: 9 });
    const visible = stage && stage.appearance !== 'absent';
    const native = spec?.geometry === 'native';
    capture.imageDetails = { schemaVersion: 1, complete: true, totalPlacements: visible ? 1 : 0,
        placements: visible ? [{ index: 0, placementId: '11111111-2222-3333-4444-555555555555', col: 3, viewportRow: 4,
            widthCells: native ? 2 : 4, heightCells: native ? 1 : 2, clipTopRows: 0, clipBottomRows: 0, clipRightColumns: 0,
            preserveAspectRatio: spec.geometry === 'aspect', bitmapWidthPx: 16, bitmapHeightPx: native ? 18 : 16,
            sixelPlacement: native ? { reservationWidthPx: 16, reservationHeightPx: 18, dimensionOrigin: 'sixel-geometry',
                cellWidthPx: 8, cellHeightPx: 18, cellWidthOrigin: 'sixel-override', cellHeightOrigin: 'sixel-override',
                arithmetic: 'float32-ceiling-v1' } : null }] : [] };
    return capture;
}
export function imageOwnership(rows = 10, cols = 60, runId = inputRunId) {
    const { before } = inputActionProof({ kind: 'keyDown', key: 'A', modifiers: 0 }, defaultInputModes, runId, inputPaneId, rows, cols);
    return { app: before.app, worker: before.worker, tty: '/dev/ttys100', terminalOutput: before.terminalOutput, at: '2026-11-04T14:00:00.000Z' };
}
export function syntheticImageReply(item, stage, state, nextId) {
    if (!stage.reply) return '';
    if (stage.reply === 'allocate') return `\x1b_Gi=${nextId},I=${state.number};OK\x1b\\`;
    if (stage.reply === 'missing') return `\x1b_Gi=0,I=${state.number},p=1;ENOENT:Image not found\x1b\\`;
    if (stage.reply === 'place-old') return `\x1b_Gi=${state.firstId},p=1;OK\x1b\\`;
    if (!state.ids.length) return `\x1b_Gi=0,I=${state.number},p=1;ENOENT:Image not found\x1b\\`;
    return `\x1b_Gi=${state.ids.at(-1)},I=${state.number},p=1;OK\x1b\\`;
}
function recordSyntheticAllocation(stage, skipped, raw, state, allocated, item) {
    if (!stage || skipped) return allocated;
    const reply = imageReply(raw, item, stage, state);
    if (reply.id === null) return allocated;
    state.ids.push(reply.id);
    if (['store', 'store-first'].includes(stage.id)) state.firstId = reply.id;
    return allocated + 1;
}
export function syntheticImageJournal(output, { runId = inputRunId, paneId = inputPaneId, initial = initialInputScreen(10, 60), mutateReply, pixelOptions } = {}) {
    const bound = imageBoundScreen(initial, runId), frames = [], actions = [], states = new Map(); let allocated = 99;
    mkdirSync(join(output, 'image-pixels')); mkdirSync(join(output, 'image-inputs'));
    for (const input of imageInputs()) writeFileSync(join(output, input.file), input.bytes);
    const ownership = imageOwnership(initial.rows, initial.cols, runId);
    const add = (entry, screen, sample = null, action = null) => {
        const item = staticImageCases.find(value => value.id === entry.caseId), stage = item?.stages.find(value => value.id === entry.stageId);
        const capture = syntheticImageCapture(screen, item, stage, pixelOptions?.(entry));
        const bytes = Buffer.from(capture.pngBase64, 'base64'), { pngBase64, ...facts } = capture;
        const pixels = [0, 1].map(side => {
            const file = `image-pixels/frame-${String(frames.length).padStart(3, '0')}-${side}.png`;
            writeFileSync(join(output, file), bytes); return { ...structuredClone(facts), file, sha256: pngHash(bytes) };
        });
        frames.push({ index: frames.length, kind: 'images-' + entry.kind, caseId: entry.caseId ?? null, stageId: entry.stageId ?? null,
            paneId, screen: structuredClone(screen), afterScreen: structuredClone(screen), sha256: snapshotHash(screen),
            afterSha256: snapshotHash(screen), pixels, ownership: structuredClone(ownership), actionIndex: action?.index ?? null, capture: sample });
    };
    add({ kind: 'start' }, initial);
    for (const entry of imagePlan) {
        if (entry.kind === 'read' && !states.has(entry.caseId)) states.set(entry.caseId, { number: 1000 + states.size, firstId: null, ids: [] });
        const state = states.get(entry.caseId) ?? {}, expected = imageAction(entry, state, runId, initial.cursorVisible);
        const action = { index: actions.length, kind: 'images-' + entry.kind, caseId: entry.caseId ?? null, stageId: entry.stageId ?? null,
            commandHex: Buffer.from(expected.command).toString('hex'), commandSha256: pngHash(Buffer.from(expected.command)),
            skipReason: expected.skipReason, number: state.number ?? null, firstId: state.firstId ?? null, ownership: structuredClone(ownership) };
        actions.push(action);
        const item = staticImageCases.find(value => value.id === entry.caseId), stage = item?.stages.find(value => value.id === entry.stageId);
        let raw = stage && !expected.skipReason ? syntheticImageReply(item, stage, state, allocated) : '';
        raw = mutateReply?.(entry, raw) ?? raw;
        allocated = recordSyntheticAllocation(stage, expected.skipReason, raw, state, allocated, item);
        const background = imageBackgroundReply(entry.kind === 'finish' ? 0xff000000 : 0xff101010);
        const sample = inputCapture(Buffer.from(raw + background).toString('hex') + imageModeReplyHex, 100 + action.index);
        add(entry, entry.kind === 'finish' ? initial : bound, sample, action);
    }
    const final = frames.at(-1).capture;
    const seal = { failed: false, disposed: true, attempts: 30, ownership: structuredClone(ownership), lastAttempt: { completed: true, fenceRequested: true,
        hex: final.hex + Buffer.from(final.barrier).toString('hex'), nonces: final.nonces, error: null } };
    return { frames, actions, seal, runId };
}
