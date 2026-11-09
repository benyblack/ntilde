// Synthetic, fully valid erase-character / saved-lines grading evidence for portable tests. Every frame, run-log entry and pinned
// result is produced through the same production validators used for real measurements. The expected viewport, saved lines and cell
// attributes are computed here with plain string and array operations independent of the contracts and of the byte-interpreting model,
// so these fixtures are a second oracle, not a mirror; negative tests perturb the returned states.
import { snapshotHash } from '../agent-observer.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { makeEraseCharsHistoryContracts, eraseCharsHistoryReset, eraseCharsHistoryNoncePrefix, eraseCharsHistoryObservation,
    gradeEraseCharsHistoryCase } from '../erase-chars-history-contracts.mjs';
import { replayEraseCharsHistoryPinned } from '../erase-chars-history-grading.mjs';
import { eraseCharsHistoryAdapterId } from '../erase-chars-history-grading-collector.mjs';
import { paletteBinding, colorCell, plainCell, syntheticSgrPaletteScreen } from './sgr-palette-evidence.mjs';

export { paletteBinding, colorCell, plainCell } from './sgr-palette-evidence.mjs';

/** Synthetic geometry: above every contract floor, small enough for fast tests. */
export const echRows = 16;
export const echCols = 60;
const geometry = Object.freeze({ rows: echRows, cols: echCols });
const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';
const pen = { kind: 'palette', index: 4 };
const bold = char => colorCell(char, { attrs: ['bold'] });

/** The independent oracle of a row after ECH: `count` cells blanked from `col`, no shifting. */
export const erasedRow = (text, col, count, width = echCols) => {
    const cells = [...text.padEnd(width, ' ')];
    for (let at = col; at < Math.min(col + count, width); at++) cells[at] = ' ';
    return cells.join('').trimEnd();
};
/** The independent oracle of a row after DCH: the suffix pulled left. */
export const shiftedRow = (text, col, count, width = echCols) => (text.padEnd(width, ' ').slice(0, col) + text.padEnd(width, ' ').slice(col + count)).trimEnd();

/** A pristine state: blank viewport, cursor home, full margins, main screen, empty history, default storage. */
export const blankState = (rows = echRows) => ({ lines: Array.from({ length: rows }, () => ''), cursor: [0, 0], top: 0, bottom: rows - 1,
    alternate: false, history: [], cells: {} });
const rowsOf = (map, rows = echRows) => Array.from({ length: rows }, (_, row) => map[row] ?? '');
const stateOf = (map, cursor, extra = {}) => ({ ...blankState(), lines: rowsOf(map), cursor, ...extra });

/** One complete valid observation screen of a state; `cells` overrides plain storage by `row,col`; `details` overrides buffer-state fields. */
export function syntheticEchScreen(state, binding = paletteBinding(), details = {}) {
    const blank = plainCell(' ');
    const grid = Array.from({ length: echRows * echCols }, () => blank);
    state.lines.forEach((line, row) => [...line].forEach((char, col) => { if (char !== ' ') grid[row * echCols + col] = plainCell(char); }));
    for (const [key, spec] of Object.entries(state.cells ?? {})) { const [row, col] = key.split(',').map(Number); grid[row * echCols + col] = spec; }
    const lines = Array.from({ length: echRows }, (_, row) => grid.slice(row * echCols, (row + 1) * echCols).map(spec => spec.char).join('').trimEnd());
    const screen = syntheticSgrPaletteScreen(grid, binding, geometry);
    return { ...screen, lines, cursorRow: state.cursor[0], cursorCol: state.cursor[1], cursorVisible: true,
        bufferState: { scrollTop: state.top, scrollBottom: state.bottom, originMode: false, autoWrap: true, insertMode: false, pendingWrap: false,
            alternateScreen: state.alternate, scrollbackLines: state.history.length, ...state.details, ...details },
        history: { startLine: 0, totalLines: state.history.length, lines: [...state.history] } };
}

/** The independently derived state of every read of the ECH case (a conclusive, supported run), by role. */
export function echStates() {
    const edgeRow = ' '.repeat(echCols - 3) + 'WXY';
    const wideRows = { 0: 'ABOVE-ROW!', 1: 'ABCDEFGHIJ', 2: 'BELOW-ROW!' };
    const attrText = 'ABCDEFG', boldAt = new Set([0, 1, 3, 4]);
    const retained = (char, col) => (boldAt.has(col) ? bold(char) : plainCell(char));
    const attrCells = Object.fromEntries([...attrText].map((char, col) => [`0,${col}`, retained(char, col)]));
    const afterCells = Object.fromEntries([...attrText].map((char, col) => [`0,${col}`,
        [2, 3, 4].includes(col) ? colorCell(' ', { bg: pen }) : retained(char, col)]));
    return {
        initial: stateOf({}, [0, 0]),
        seed: stateOf({ 0: 'ABCDE' }, [0, 0]),
        erased: stateOf({ 0: erasedRow('ABCDE', 0, 3) }, [0, 0]),
        omitted: stateOf({ 0: erasedRow('ABCDE', 0, 1) }, [0, 0]),
        explicit0: stateOf({ 0: erasedRow('ABCDE', 0, 1) }, [0, 0]),
        explicit1: stateOf({ 0: erasedRow('ABCDE', 0, 1) }, [0, 0]),
        explicit5: stateOf({ 0: erasedRow('ABCDE', 0, 5) }, [0, 0]),
        wideBefore: stateOf(wideRows, [1, 2]),
        wide: stateOf({ ...wideRows, 1: erasedRow('ABCDEFGHIJ', 2, 3) }, [1, 2]),
        wideOversized: stateOf({ ...wideRows, 1: erasedRow('ABCDEFGHIJ', 2, 999) }, [1, 2]),
        edgeBefore: stateOf({ 0: edgeRow }, [0, echCols - 3]),
        edge: stateOf({ 0: erasedRow(edgeRow, echCols - 3, 2) }, [0, echCols - 3]),
        attrBefore: stateOf({ 0: attrText }, [0, 2], { cells: attrCells }),
        attr: stateOf({ 0: erasedRow(attrText, 2, 3) }, [0, 2], { cells: afterCells }),
        pen: stateOf({ 0: 'X' }, [0, 1], { cells: { '0,0': colorCell('X', { bg: pen }) } }),
    };
}

/** Rows HIST-first .. HIST-last on consecutive viewport rows from row 0, blank elsewhere. */
const hist = (first, last) => Object.fromEntries(Array.from({ length: last - first + 1 }, (_, index) => [index, `HIST-${first + index}`]));
const histLines = (count) => Array.from({ length: count }, (_, index) => `HIST-${index}`);
/** The independently derived state of every read of the ED 3 case (a conclusive, supported run), by role. */
export function historyStates() {
    const last = echRows + 7;                       // the last HIST line written is HIST-(rows+7): rows+8 lines in all
    const seeded = stateOf(hist(9, last), [6, 11], { history: histLines(9) });
    const marks = Object.fromEntries(Array.from({ length: echRows }, (_, row) => [row, `ROW-${row}`]));
    return {
        initial: stateOf({}, [0, 0]),
        before: stateOf({}, [echRows - 1, 0], { history: ['', '', ''] }),
        after: stateOf({}, [echRows - 1, 0]),
        seed: seeded,
        erase3: { ...seeded, history: [] },
        ed2: stateOf({}, [6, 11], { history: histLines(9) }),
        scrolled: stateOf(hist(12, last), [echRows - 1, 0], { history: histLines(12) }),
        emptyBefore: stateOf(marks, [6, 11]),
        emptyErase: stateOf(marks, [6, 11]),
        regrow: stateOf(hist(11, last), [echRows - 1, 0], { history: ['HIST-9', 'HIST-10'] }),
    };
}

const frame = (index, kind, screen, barrier) => ({ index, kind, paneId, barrier, screen, sha256: snapshotHash(screen) });

/** The synthetic contracts at the synthetic geometry. */
export const syntheticEraseCharsHistoryContracts = () => makeEraseCharsHistoryContracts(echRows, echCols);

/** The observed screens of one case; `edit(role, state)` may return a replacement state (negative evidence). */
export function syntheticEraseCharsHistoryScreens(contract, binding, edit) {
    binding ??= paletteBinding();
    edit ??= (_role, state) => state;
    const states = contract.family === 'erase-character' ? echStates() : historyStates();
    return contract.roles.map(role => syntheticEchScreen(edit(role.role, structuredClone(states[role.role])), binding));
}

/** A complete valid synthetic run: frames, worker run log and adapter facts. `edit(caseId, role, state)` builds negative evidence. */
export function syntheticEraseCharsHistoryEvidence(runId, bundleSha256, binding, edit) {
    runId ??= 'a'.repeat(32);
    bundleSha256 ??= 'f'.repeat(64);
    binding ??= paletteBinding();
    const contracts = syntheticEraseCharsHistoryContracts();
    const nonce = eraseCharsHistoryNoncePrefix + runId;
    const blank = syntheticEchScreen(blankState(), binding);
    const bindScreen = syntheticEchScreen({ ...blankState(), lines: [nonce, ...blankState().lines.slice(1)], cursor: [0, nonce.length] }, binding);
    const frames = [frame(0, 'echh-start', blank, null), frame(1, 'echh-bind', bindScreen, deviceAttribute)];
    const cases = contracts.map(contract => {
        const feeds = planFeeds(contract);
        const barriers = feeds.map(() => deviceAttribute);
        const screens = syntheticEraseCharsHistoryScreens(contract, binding, edit ? (role, state) => edit(contract.caseId, role, state) : undefined);
        for (const screen of screens) frames.push(frame(frames.length, 'echh-read', screen, deviceAttribute));
        const pinned = { probeId: contract.caseId, ...replayEraseCharsHistoryPinned(contract, screens) };
        return { caseId: contract.caseId, feeds, barriers,
            reads: screens.map(screen => ({ kind: 'state', cursor: [screen.cursorRow, screen.cursorCol] })), pinned };
    });
    frames.push(frame(frames.length, 'echh-finish', blank, deviceAttribute));
    const run = { schemaVersion: 1, adapterId: eraseCharsHistoryAdapterId, probeIds: contracts.map(contract => contract.caseId), cases };
    const adapter = { id: eraseCharsHistoryAdapterId, derivedFromAdapterId: 'ntilde-owned-cursor-qualification-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, reset: eraseCharsHistoryReset };
}

/** The independent contract verdict for one synthetic case. */
export function syntheticEraseCharsHistoryGrade(contract, binding, edit) {
    return gradeEraseCharsHistoryCase(contract, eraseCharsHistoryObservation(contract, syntheticEraseCharsHistoryScreens(contract, binding, edit)));
}
