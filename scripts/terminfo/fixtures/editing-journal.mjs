// Synthetic logical records for portable refusal tests, never runtime measurements.
import { makeEditingCases } from '../editing-cases.mjs';
import { compressExpected } from '../editing-expectations.mjs';
import { screenSnapshot, snapshotHash } from '../agent-observer.mjs';
export const editingPaneId = '11111111-1111-1111-1111-111111111111';
const barrier = '\x1b[?1;2c';
export function editingWire(stage) {
    const expected = compressExpected(stage), cellDetails = [];
    for (const run of expected.expectedAttributeRuns) {
        const values = { ...run.values }; delete values.flagsMask; delete values.flagsValue;
        const value = { overline: false, flags: 256, fg: -1, foregroundArgb: null, underlineStyle: 0,
            underlineColorIndex: -1, underlineArgb: null, ...values };
        const last = cellDetails.at(-1);
        if (last && JSON.stringify(last.value) === JSON.stringify(value)) last.count += run.count;
        else cellDetails.push({ count: run.count, value });
    }
    return { rows: stage.grid.length, cols: stage.grid[0].length, lines: expected.lines, cursorRow: expected.cursorRow, cursorCol: expected.cursorCol, cursorVisible: true,
        cursorMode: { style: 2, blink: true, isDefault: true }, bufferState: expected.bufferState,
        history: expected.history, cellDetails: cellDetails.map(({ count, value }) => ({ count, ...value })) };
}
export function initialEditingScreen(rows = 10, cols = 48) {
    return editingWire({ grid: Array.from({ length: rows }, () => Array.from({ length: cols }, () => ({ text: ' ', overline: false,
        flags: 768, fg: -1, bg: -1, foregroundArgb: null, backgroundArgb: null, underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null }))),
        row: 0, col: 0, top: 0, bottom: rows - 1, alternate: false });
}
/** Build coherent unit-only records for refusal tests; these are never runtime measurements. */
export function syntheticCellPlanJournal(prefix, runId, { casesFor, nonce, rows = 10, cols = 48 }) {
    const frames = [], add = (kind, raw, caseId = null, stageId = null) => {
        const screen = screenSnapshot(raw, false, true, true, true, true), sha256 = snapshotHash(screen);
        frames.push({ index: frames.length, kind: prefix + '-' + kind, paneId: editingPaneId,
            barrier: kind === 'start' ? null : barrier, screen, sha256, afterScreen: structuredClone(screen), afterSha256: sha256, caseId, stageId });
    };
    add('start', initialEditingScreen(rows, cols));
    const bind = initialEditingScreen(rows, cols); bind.lines[0] = nonce + runId;
    bind.cursorCol = 42; bind.bufferState.alternateScreen = true; add('bind', bind);
    for (const item of casesFor(rows, cols).filter(value => value.id.startsWith(prefix + '.'))) {
        add('read', editingWire(item.before), item.id, 'control'); add('read', editingWire(item.after), item.id, 'after');
    }
    add('finish', initialEditingScreen(rows, cols)); return frames;
}

export const syntheticEditingJournal = (prefix, runId, geometry = {}) => syntheticCellPlanJournal(prefix, runId,
    { casesFor: makeEditingCases, nonce: prefix === 'erase' ? 'NTILDE_ER_' : 'NTILDE_EC_', ...geometry });
