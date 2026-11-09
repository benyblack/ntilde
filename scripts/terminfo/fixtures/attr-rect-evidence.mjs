// Synthetic, fully valid attribute-rectangle grading evidence for portable tests. Every frame, run-log entry and pinned result is produced
// through the same production validators used for real measurements. The screens come from the contracts' independent oracle (the VT510
// definitions), and the tests additionally pin literal expected marks so the fixtures are a second oracle for the pinned fixtures themselves.
import { snapshotHash } from '../agent-observer.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { attrRectContracts, attrRectObservation, gradeAttrRectCase, attrRectOracle } from '../attr-rect-contracts.mjs';
import { replayAttrRectPinned } from '../attr-rect-grading.mjs';
import { attrRectAdapterId } from '../attr-rect-grading-collector.mjs';
import { syntheticSavedResetScreen } from './saved-reset-evidence.mjs';
import { paletteBinding, colorCell, paletteChannel } from './sgr-palette-evidence.mjs';

const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';
const { applyOp, plain4, fix4x8, pinnedC, pinnedR, reverseMix, operandSeed, setScenarios, extentOp, seed } = attrRectOracle;
const op = (top, left, bottom, right, selectors, extra = {}) => ({ top, left, bottom, right, selectors, reverse: false, rectangle: false, ...extra });

/** One synthetic screen from an oracle grid: `{ grid, cursor, buffer }` (the harness runs on the alternate screen). */
export function syntheticAttrRectScreen({ grid = new Map(), cursor = [0, 0], buffer = {} } = {}, binding = paletteBinding()) {
    const cells = [...grid].map(([place, cell]) => {
        const [row, col] = place.split(',').map(Number);
        const attrs = cell.attrs.filter(name => name !== 'underline');
        return { row, col, spec: colorCell(cell.char, { attrs, underlineStyle: cell.attrs.includes('underline') ? 1 : 0,
            ...(cell.fg === undefined ? {} : { fg: paletteChannel(cell.fg) }), ...(cell.bg === undefined ? {} : { bg: paletteChannel(cell.bg) }) }) };
    });
    return syntheticSavedResetScreen({ cells, cursor, alt: true, buffer }, binding);
}

const stream = (grid, item) => applyOp(grid, { ...item, rectangle: false });
const rect = (grid, item) => applyOp(grid, { ...item, rectangle: true });
const read = (grid, cursor, buffer) => ({ grid, cursor, buffer });
const pinnedOp = reverse => op(1, 1, 3, 5, [7], { reverse });

/** The reads of every group of one synthetic case (a conclusive, correctly behaving App), keyed by role. */
export function syntheticAttrRectReads(caseId) {
    const blank = read(new Map(), [0, 0]);
    if (caseId === 'editing.decsace') {
        const item = extentOp(), reverse = extentOp(true);
        const streamed = read(stream(fix4x8.grid, item), [0, 0]), rectangle = read(rect(fix4x8.grid, item), [0, 0]);
        const penGrid = new Map(fix4x8.grid);
        penGrid.set('2,2', { char: 'Q', attrs: ['bold'], bg: 4 });
        return { initial: blank, pinned: blank, fixBefore: read(fix4x8.grid, [0, 0]), stream0: streamed, stream1: streamed, omitted: streamed, rectangle, ignored: rectangle,
            rectReverse: read(rect(fix4x8.grid, reverse), [0, 0]), streamReverse: read(stream(fix4x8.grid, reverse), [0, 0]),
            isolation: read(fix4x8.grid, [2, 2], { scrollTop: 1, scrollBottom: 2, originMode: true }), isolationPen: read(penGrid, [2, 3], { scrollTop: 1, scrollBottom: 2, originMode: true }),
            witness: streamed };
    }
    const reverse = caseId === 'editing.decrara';
    const initialGrid = reverse ? pinnedR.grid : pinnedC.grid;
    const reads = { initial: blank, seed: read(initialGrid, [2, 6]), cursor: read(initialGrid, [0, 0]), edited: read(stream(initialGrid, pinnedOp(reverse)), [0, 0]),
        rect: read(rect(initialGrid, pinnedOp(reverse)), [0, 0]), plainBefore: read(plain4.grid, [0, 0]),
        bounds: read(rect(plain4.grid, op(2, 2, 3, 5, [7], { reverse })), [0, 0]), witness: read(stream(initialGrid, pinnedOp(reverse)), [0, 0]) };
    const penGrid = rect(plain4.grid, op(1, 1, 2, 3, [7], { reverse }));
    penGrid.set('3,0', { char: 'P', attrs: ['bold'], bg: 4 });
    reads.pen = read(penGrid, [3, 1]);
    if (reverse) {
        const toggled = rect(reverseMix.seed.grid, op(1, 1, 2, 6, [7], { reverse: true }));
        reads.mixBefore = read(reverseMix.seed.grid, [5, 10]);
        reads.mix = read(toggled, [5, 10]);
        reads.mixAgain = read(reverseMix.seed.grid, [5, 10]);
        reads.opsBefore = read(operandSeed.grid, [5, 10]);
        const operands = [op(1, 1, 1, 6, [1]), op(2, 1, 2, 6, [0]), op(3, 1, 3, 6, []), op(4, 1, 4, 6, [22, 24, 25, 27]), op(5, 1, 5, 6, [1, 1]), op(6, 1, 6, 6, [4, 5])]
            .reduce((grid, item) => rect(grid, { ...item, reverse: true }), operandSeed.grid);
        reads.ops = read(operands, [5, 10]);
        return reads;
    }
    reads.unknown = read([op(1, 1, 1, 3, [99]), op(2, 1, 2, 3, [1, 99, 4]), op(3, 1, 3, 3, [8])].reduce((grid, item) => rect(grid, item), plain4.grid), [0, 0]);
    for (const item of setScenarios) {
        const buffer = item.margins ? { scrollTop: item.margins[0], scrollBottom: item.margins[1], originMode: true } : undefined;
        reads[item.before] = read(item.seed.grid, item.cursor, buffer);
        reads[item.after] = read((item.absolute ?? item.ops).reduce((grid, step) => rect(grid, step), item.seed.grid), item.cursor, buffer);
    }
    return reads;
}

/** The screens of every read group of one synthetic case, in plan order. */
export function syntheticAttrRectScreens(caseId, binding = paletteBinding(), readsOf = null) {
    const reads = readsOf?.(caseId) ?? syntheticAttrRectReads(caseId);
    const contract = attrRectContracts.find(item => item.caseId === caseId);
    return contract.roles.map(role => syntheticAttrRectScreen(reads[role], binding));
}

/** One journaled frame with its digest. */
const frame = (index, kind, screen, barrier) => ({ index, kind, paneId, barrier, screen, sha256: snapshotHash(screen) });

/** A complete, valid synthetic run: frames, worker run log and adapter facts. `readsOf(caseId)` may override the per-case reads. */
export function syntheticAttrRectEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), readsOf = null) {
    const nonce = 'NTILDE_AR_' + runId;
    const blank = syntheticAttrRectScreen({}, binding);
    const bound = { ...syntheticAttrRectScreen({}, binding), lines: [nonce, ...Array.from({ length: blank.rows - 1 }, () => '')], cursorCol: nonce.length };
    const frames = [frame(0, 'atrc-start', blank, null), frame(1, 'atrc-bind', bound, deviceAttribute)];
    const cases = attrRectContracts.map(contract => {
        const feeds = planFeeds(contract);
        const barriers = feeds.map(() => deviceAttribute);
        const screens = syntheticAttrRectScreens(contract.caseId, binding, readsOf);
        for (const screen of screens) frames.push(frame(frames.length, 'atrc-read', screen, deviceAttribute));
        const pinned = { probeId: contract.caseId, ...replayAttrRectPinned(contract, screens) };
        const record = { caseId: contract.caseId, feeds, barriers, reads: screens.map(screen => ({ kind: 'cells', cursor: [screen.cursorRow, screen.cursorCol] })),
            pinnedFed: contract.pinnedFeeds.length, pinned };
        if (contract.rectanglePinnedReads) {
            record.pinnedRectangle = { probeId: contract.caseId, ...replayAttrRectPinned(contract, screens, contract.rectanglePinnedReads) };
            record.pinnedRectangleFed = contract.pinnedFeeds.length;
        }
        return record;
    });
    frames.push(frame(frames.length, 'atrc-finish', blank, deviceAttribute));
    const run = { schemaVersion: 1, adapterId: attrRectAdapterId, probeIds: attrRectContracts.map(contract => contract.caseId), cases };
    const adapter = { id: attrRectAdapterId, derivedFromAdapterId: 'ntilde-owned-saved-reset-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter };
}

/** The independent contract verdict for one synthetic case. */
export function syntheticAttrRectGrade(caseId, binding = paletteBinding(), readsOf = null) {
    const contract = attrRectContracts.find(item => item.caseId === caseId);
    return gradeAttrRectCase(contract, attrRectObservation(contract, syntheticAttrRectScreens(caseId, binding, readsOf)));
}
export { seed };
export { paletteBinding, colorCell, paletteChannel } from './sgr-palette-evidence.mjs';
