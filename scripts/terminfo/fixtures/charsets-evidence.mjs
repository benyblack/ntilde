// Synthetic, fully valid charset grading evidence for portable tests. Every frame, run-log entry and pinned result is
// produced through the same production validators used for real measurements. The expected rows below are typed here
// independently of the contracts, so the fixtures are a second oracle, not a mirror. A row is a string: every character
// is one narrow cell, except CJK ideographs (a wide cell plus its continuation) and a combining mark U+0301 (stored as
// extended text on the previous cell). A private-use U+E000 before an ideograph stores it narrow, as a cell-position mistake.
import { snapshotHash } from '../agent-observer.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { charsetsContracts, charsetsObservation, gradeCharsetsCase } from '../charsets-contracts.mjs';
import { replayCharsetsPinned } from '../charsets-grading.mjs';
import { charsetsAdapterId } from '../charsets-grading-collector.mjs';

/** Synthetic geometry, above both the observer's floor and the contract minimum. */
export const charsetsRows = 6;
export const charsetsCols = 60;
const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';
const flagBits = { defaultColors: 0x300, wide: 0x800, continuation: 0x1000, extended: 0x2000 };

/** The physical cells of one row string. */
export function rowCells(text) {
    const cells = [];
    let narrow = false;
    for (const char of text) {
        const code = char.codePointAt(0);
        if (code === 0xe000) { narrow = true; continue; }
        if (code === 0x301) { cells.at(-1).extended = true; cells.at(-1).text += char; continue; }
        if (code >= 0x4e00 && code <= 0x9fff && !narrow) {
            cells.push({ text: char, flags: flagBits.defaultColors | flagBits.wide });
            cells.push({ text: '', flags: flagBits.defaultColors | flagBits.continuation });
        } else cells.push({ text: char, flags: flagBits.defaultColors });
        narrow = false;
    }
    return cells.map(cell => ({ ...cell, flags: cell.flags | (cell.extended ? flagBits.extended : 0) }));
}

const detailOf = flags => ({ flags, fg: -1, bg: -1, foregroundArgb: null, backgroundArgb: null,
    underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null });
/** Coalesce adjacent identical runs so fixtures stay canonical. */
function coalesce(runs) {
    const out = [];
    for (const run of runs) {
        const previous = out.at(-1);
        if (previous && JSON.stringify({ ...previous, count: 0 }) === JSON.stringify({ ...run, count: 0 })) previous.count += run.count;
        else out.push({ ...run });
    }
    return out;
}

/** A complete valid observation screen: `rows` are row strings, `cursor` is [row, col]; `edit(details)` may tamper with the per-cell flags. */
export function syntheticCharsetsScreen(rows, cursor, edit = null) {
    const details = [];
    const lines = [];
    for (let row = 0; row < charsetsRows; row++) {
        const cells = rowCells(rows[row] ?? '');
        lines.push(cells.filter(cell => cell.flags !== (flagBits.defaultColors | flagBits.continuation)).map(cell => cell.text).join('').trimEnd());
        for (let col = 0; col < charsetsCols; col++) details.push(detailOf(cells[col]?.flags ?? flagBits.defaultColors));
    }
    if (edit) edit(details);
    return { rows: charsetsRows, cols: charsetsCols, lines, cursorRow: cursor[0], cursorCol: cursor[1], cursorVisible: true,
        cellDetails: coalesce(details.map(detail => ({ count: 1, ...detail }))) };
}

const dec = ['┘┐┌└─│┌', 'jklmqxl'];
/** The witness rows (rows 1-3) appended to a fixture row, typed independently. */
const withWitness = (row0, rows) => ({
    restored: { rows: [row0], cursor: null },
    witnessAscii: { rows: [row0, dec[1]], cursor: [1, 7] },
    witnessDec: { rows: [row0, dec[1], dec[0]], cursor: [2, 7] },
    witnessAfter: { rows: [row0, dec[1], dec[0], dec[1]], cursor: [3, 7] },
    ...rows,
});
const one = (row, cursor) => ({ rows: [row], cursor });

/** The cell rows of every read group of one synthetic case (a conclusive, supported run), by role. */
export function syntheticCharsetsReads(caseId) {
    const initial = { rows: [], cursor: [0, 0] };
    const reads = {
        'charsets.dec-special': () => {
            const target = 'q─q';
            const group = withWitness(target, {});
            group.restored.cursor = [0, 3];
            return { initial, seed: one('qqq', [0, 3]), cup: one('qqq', [0, 1]), target: one(target, [0, 3]), ...group,
                designateG1: one('qqq', [0, 3]), upper: one('QQQ', [0, 3]), persist: one('───', [0, 3]),
                persistRestore: one('───qqq', [0, 6]) };
        },
        'charsets.dec-line-drawing': () => {
            const target = '┘┐┌└─│j';
            const group = withWitness(target, {});
            group.restored.cursor = [0, 6];
            return { initial, seed: one('jklmqxj', [0, 7]), cup: one('jklmqxj', [0, 0]), target: one(target, [0, 6]), ...group,
                designateG1: one('jklmqx', [0, 6]), upper: one('JKLMQX', [0, 6]), persist: one('┘┐┌└─│', [0, 6]),
                persistRestore: one('┘┐┌└─│jklmqx', [0, 12]) };
        },
        'charsets.g0-g1-switching': () => {
            const target = 'Al┌lZ';
            const group = withWitness(target, {});
            group.restored.cursor = [0, 4];
            return { initial, seed: one('AlllZ', [0, 5]), cup: one('AlllZ', [0, 1]), target: one(target, [0, 4]), ...group,
                designateOnly: one('AlllZ', [0, 4]), shiftOnly: one('AlllZ', [0, 4]), g0Direct: one('A┌┌┌Z', [0, 4]),
                g1Invoked: one('A┌┌┌Z', [0, 4]), persistFixture: one(target, [0, 4]),
                persistShift: { rows: [target, '┘┐┌'], cursor: [1, 3] } };
        },
        'charsets.utf8': () => {
            const target = 'Aé世Z';
            const group = withWitness(target, {});
            delete group.restored;
            return { initial, seed: one('Axy?Z', [0, 5]), cup1: one('Axy?Z', [0, 1]), afterE: one('Aéy?Z', [0, 2]), cup2: one('Aéy?Z', [0, 2]),
                target: one(target, [0, 4]), ...group, wideAlone: one('A世Z', [0, 4]), precomposed: one('éZ', [0, 2]),
                decomposed: one('éZ', [0, 2]) };
        },
    }[caseId];
    return reads();
}

/** The screens of every read group of one synthetic case; `readsOf(caseId)` may override the groups to build negative evidence. */
export function syntheticCharsetsScreens(caseId, readsOf = null) {
    const reads = readsOf?.(caseId) ?? syntheticCharsetsReads(caseId);
    const contract = charsetsContracts.find(item => item.caseId === caseId);
    return contract.roles.map(role => {
        const read = reads[role];
        if (!read) throw new Error(`Synthetic ${caseId} has no ${role} group.`);
        return syntheticCharsetsScreen(read.rows, read.cursor, read.edit ?? null);
    });
}

/** One journaled frame with its digest. */
const frame = (index, kind, screenValue, barrier) => ({ index, kind, paneId, barrier, screen: screenValue, sha256: snapshotHash(screenValue) });

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export function syntheticCharsetsEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), readsOf = null) {
    const nonce = 'NTILDE_CS_' + runId;
    const blank = syntheticCharsetsScreen([], [0, 0]);
    const bindScreen = syntheticCharsetsScreen([nonce], [0, nonce.length]);
    const frames = [frame(0, 'chrs-start', blank, null), frame(1, 'chrs-bind', bindScreen, deviceAttribute)];
    const cases = charsetsContracts.map(contract => {
        const feeds = planFeeds(contract);
        const barriers = feeds.map(() => deviceAttribute);
        const screens = syntheticCharsetsScreens(contract.caseId, readsOf);
        for (const screen of screens) frames.push(frame(frames.length, 'chrs-read', screen, deviceAttribute));
        const pinned = { probeId: contract.caseId, ...replayCharsetsPinned(contract, screens) };
        return { caseId: contract.caseId, feeds, barriers,
            reads: screens.map(screen => ({ kind: 'cells', cursor: [screen.cursorRow, screen.cursorCol] })), pinned };
    });
    frames.push(frame(frames.length, 'chrs-finish', blank, deviceAttribute));
    const run = { schemaVersion: 1, adapterId: charsetsAdapterId, probeIds: charsetsContracts.map(contract => contract.caseId), cases };
    const adapter = { id: charsetsAdapterId, derivedFromAdapterId: 'ntilde-owned-erase-line-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter };
}

/** The independent contract verdict for one synthetic case. */
export function syntheticCharsetsGrade(caseId, readsOf = null) {
    const contract = charsetsContracts.find(item => item.caseId === caseId);
    return gradeCharsetsCase(contract, charsetsObservation(contract, syntheticCharsetsScreens(caseId, readsOf)));
}
