// Synthetic records for portable validators; these are never runtime measurements.
import { initialEditingScreen, editingPaneId } from './editing-journal.mjs';
import { compressSavedExpected } from '../saved-expectations.mjs';
import { makeSavedCases } from '../saved-cases.mjs';
import { screenSnapshot, snapshotHash } from '../agent-observer.mjs';
export function savedWire(state) {
    const expected = compressSavedExpected(state);
    return { rows: state.grid.length, cols: state.grid[0].length, lines: expected.lines,
        cursorRow: expected.cursorRow, cursorCol: expected.cursorCol, cursorVisible: true,
        cursorMode: expected.cursorMode, bufferState: expected.bufferState, history: expected.history,
        cellDetails: expected.expectedAttributeRuns.map(run => ({ count: run.count, ...run.values })) };
}
export function syntheticSavedJournal(runId = 'a'.repeat(32), { rows = 12, cols = 60 } = {}) {
    const frames = [], add = (kind, raw, caseId = null, stageId = null) => {
        const screen = screenSnapshot(raw, false, true, true, true, true), sha256 = snapshotHash(screen);
        frames.push({ index: frames.length, kind: 'saved-' + kind, paneId: editingPaneId,
            barrier: kind === 'start' ? null : '\x1b[?1;2c', screen, sha256, afterScreen: structuredClone(screen), afterSha256: sha256, caseId, stageId });
    };
    add('start', initialEditingScreen(rows, cols));
    const bound = initialEditingScreen(rows, cols); bound.lines[0] = 'NTILDE_SV_' + runId;
    bound.cursorCol = 42; bound.bufferState.alternateScreen = true; add('bind', bound);
    for (const item of makeSavedCases(rows, cols)) for (const stage of item.stages) add('read', savedWire(stage.expected), item.id, stage.id);
    add('finish', initialEditingScreen(rows, cols)); return frames;
}
