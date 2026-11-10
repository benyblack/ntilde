// Synthetic, fully valid rectangle-checksum grading evidence for portable tests. Every frame, run-log entry and pinned result is
// produced through the same production validators used for real measurements. The expected replies are summed here from plain
// text rows, a hand-written weight table and hand-written rectangles, independently of the contract's model, so the fixture is a
// second oracle, not a mirror.
import { snapshotHash } from '../agent-observer.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { rectChecksumContracts, rectChecksumObservation, gradeRectChecksumCase } from '../rect-checksum-contracts.mjs';
import { replayRectChecksumPinned } from '../rect-checksum-grading.mjs';
import { rectChecksumAdapterId } from '../rect-checksum-grading-collector.mjs';
import { syntheticColEditScreen, colEditRows, colEditCols, paletteBinding, colorCell, plainCell } from './col-edit-evidence.mjs';
import { paletteChannel, literalChannel } from './sgr-palette-evidence.mjs';

/** The synthetic geometry: thirteen rows of sixty columns. */
export const rectChecksumRows = colEditRows;
export const rectChecksumCols = colEditCols;
const W = colEditCols, H = colEditRows;
const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';

/** The attribute row (zero-based 2) as [column, cell spec, weight], written out by hand. */
const attrRow = [
    [0, plainCell('P'), 0], [2, colorCell('B', { attrs: ['bold'] }), 0x80], [4, colorCell('K', { attrs: ['blink'] }), 0x40],
    [6, colorCell('R', { attrs: ['inverse'] }), 0x20], [8, colorCell('U', { underlineStyle: 1 }), 0x10], [10, colorCell('C', { underlineStyle: 3 }), 0x10],
    [12, colorCell('D', { underlineStyle: 2 }), 0x10], [14, colorCell('X', { attrs: ['bold', 'blink', 'inverse'], underlineStyle: 1 }), 0xf0],
    [16, colorCell(' ', { attrs: ['inverse'] }), 0x20], [18, colorCell('I', { attrs: ['italic'] }), 0], [20, colorCell('F', { attrs: ['dim'] }), 0],
    [22, colorCell('H', { attrs: ['hidden'] }), 0], [24, colorCell('S', { attrs: ['strikethrough'] }), 0], [26, colorCell('O', { attrs: ['overline'] }), 0],
    [28, colorCell('r', { fg: paletteChannel(1) }), 0], [30, colorCell('g', { bg: literalChannel(10, 20, 30) }), 0], [32, colorCell('p', { fg: paletteChannel(200) }), 0],
];
const seedRow = [...'ABCDE'].map(char => plainCell(char));
/** The attribute row as cell specs, blanks between. */
const attrCells = () => {
    const cells = Array.from({ length: 33 }, () => plainCell(' '));
    for (const [col, spec] of attrRow) cells[col] = spec;
    return cells;
};

/** The value of every cell of the seeded screen: its character code (blank 0x20) plus its weight. */
function values(withAttributes) {
    const grid = Array.from({ length: H }, () => Array.from({ length: W }, () => 0x20));
    [...'ABCDE'].forEach((char, col) => { grid[0][col] = char.charCodeAt(0); });
    if (withAttributes) for (const [col, spec, weight] of attrRow) grid[2][col] = spec.char.charCodeAt(0) + weight;
    return grid;
}
/** The reply for request `id` over the inclusive rectangle, or 0000 when it is inverted. */
function reply(id, [top, left, bottom, right], grid) {
    let sum = 0;
    for (let row = top; row <= bottom; row++) for (let col = left; col <= right; col++) sum += grid[row][col];
    if (top > bottom || left > right) sum = 0;
    const hex = ((65536 - (sum % 65536)) % 65536).toString(16).toUpperCase().padStart(4, '0');
    return `\x1bP${id}!~${hex}\x1b\\`;
}

/** The expected exchange of every report role: [request id, inclusive rectangle], or null for silence. Written by hand. */
const exchanges = Object.freeze({
    pinned: [1, [0, 0, 0, 4]], terminal: [1, [0, 0, 0, 0]],
    'attr-bold': [2, [2, 2, 2, 2]], 'attr-blink': [2, [2, 4, 2, 4]], 'attr-reverse': [2, [2, 6, 2, 6]], 'attr-underline': [2, [2, 8, 2, 8]],
    'attr-curly': [2, [2, 10, 2, 10]], 'attr-double': [2, [2, 12, 2, 12]], 'attr-allFour': [2, [2, 14, 2, 14]], 'attr-reverseSpace': [2, [2, 16, 2, 16]],
    'attr-row': [2, [2, 0, 2, W - 1]], 'attr-unweighted': [2, [2, 18, 2, 32]],
    'page-omitted': [7, [0, 0, H - 1, W - 1]], 'page-zero': [7, [0, 0, H - 1, W - 1]], 'page-partial': [7, [2, 4, H - 1, W - 1]],
    'page-oversized': [7, [0, 0, H - 1, W - 1]], 'page-parameter': [7, [0, 0, 0, 4]], 'id-largest': [65535, [2, 0, 2, 39]], 'id-omitted': [0, [0, 0, 0, 4]],
    'inverted-rows': [7, [2, 0, 1, 7]], 'inverted-cols': [7, [0, 4, 0, 0]],
    'identity-private': null, 'identity-greater': null, 'identity-seventh': null, 'identity-colon': null, 'identity-dollar': null, 'identity-space': null,
    'origin-region': [7, [2, 0, 3, 11]], 'origin-clip': [7, [2, 0, H - 1, W - 1]], 'origin-defaults': [7, [2, 0, H - 1, W - 1]],
});

/** The read (screen spec and expected reply) of every role of a conclusive, supported run. */
export function syntheticRectChecksumReads() {
    const contract = rectChecksumContracts[0];
    const blank = { rows: [], cursor: [0, 0] };
    const seeded = { rows: [seedRow], cursor: [0, 0] };
    const attributed = { rows: [seedRow, [], attrCells()], cursor: [0, 0] };
    const origin = { ...attributed, cursor: [2, 0], state: { margins: [2, 5], origin: true } };
    const reads = {};
    let screen = blank;
    for (const role of contract.roles) {
        if (role === 'initial') screen = blank;
        else if (role === 'seed') screen = seeded;
        else if (role === 'attrSeed') screen = attributed;
        else if (role === 'originSeed') screen = origin;
        const exchange = exchanges[role];
        const grid = values(screen === attributed || screen === origin);
        reads[role] = { ...screen, reply: exchange === undefined ? undefined : exchange && reply(exchange[0], exchange[1], grid) };
    }
    return reads;
}

/** One journaled frame with its digest. */
const frame = (index, kind, screen, barrier, report) => ({ index, kind, paneId, barrier, screen, sha256: snapshotHash(screen), ...(report ? { report } : {}) });
/** A synthetic screen on the alternate screen the suite runs on. */
const alternate = screen => ({ ...screen, bufferState: { ...screen.bufferState, alternateScreen: true } });

/** The screens and replies of every read; `mutate(role, read)` may return a changed read to build negative evidence. */
export function syntheticRectChecksumScreens(binding = paletteBinding(), mutate = null) {
    const contract = rectChecksumContracts[0];
    const reads = syntheticRectChecksumReads();
    return contract.roles.map(role => {
        const read = mutate?.(role, structuredClone(reads[role])) ?? reads[role];
        const screen = alternate(syntheticColEditScreen(read.rows, read.cursor, read.state, [], binding));
        return { role, screen, reply: read.reply === undefined ? undefined : read.reply };
    });
}

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export function syntheticRectChecksumEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), mutate = null) {
    const contract = rectChecksumContracts[0];
    const nonce = 'NTILDE_CK_' + runId;
    const blank = syntheticColEditScreen([], [0, 0], undefined, [], binding);
    const bound = alternate({ ...blank, lines: [nonce, ...Array.from({ length: H - 1 }, () => '')], cursorCol: nonce.length });
    const frames = [frame(0, 'csum-start', blank, null), frame(1, 'csum-bind', bound, deviceAttribute)];
    const reads = syntheticRectChecksumScreens(binding, mutate);
    const replies = reads.map(read => (read.reply === undefined ? null : read.reply === null ? null : { raw: read.reply }));
    const records = reads.map((read, index) => {
        const report = read.reply === undefined ? undefined : { barrier: deviceAttribute, reply: replies[index] };
        frames.push(frame(frames.length, 'csum-read', read.screen, deviceAttribute, report));
        return { kind: report ? 'report' : 'cells', cursor: [read.screen.cursorRow, read.screen.cursorCol], ...(report ? { report: structuredClone(report) } : {}) };
    });
    frames.push(frame(frames.length, 'csum-finish', blank, deviceAttribute));
    const feeds = planFeeds(contract);
    const pinned = { probeId: contract.caseId, ...replayRectChecksumPinned(contract, replies) };
    const run = { schemaVersion: 1, adapterId: rectChecksumAdapterId, probeIds: [contract.caseId],
        cases: [{ caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute), reads: records, pinned }] };
    const adapter = { id: rectChecksumAdapterId, derivedFromAdapterId: 'ntilde-owned-column-editing-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter };
}

/** The independent contract verdict for the synthetic case. */
export function syntheticRectChecksumGrade(binding = paletteBinding(), mutate = null) {
    const contract = rectChecksumContracts[0];
    const reads = syntheticRectChecksumScreens(binding, mutate);
    return gradeRectChecksumCase(contract, rectChecksumObservation(contract, reads.map(read => read.screen), reads.map(read => read.reply ?? null)));
}
