// Synthetic unit-only state, never runtime observations.
import { editingWire, editingPaneId } from './editing-journal.mjs';
export { editingPaneId as resizePaneId };
export function resizeWire(state) {
    return { ...editingWire(state), rows: state.grid.length, cols: state.grid[0].length };
}
export function initialResizeScreen(rows = 12, cols = 60) {
    const blank = { text: ' ', overline: false, flags: 768, fg: -1, bg: -1,
        foregroundArgb: null, backgroundArgb: null, underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null };
    return resizeWire({ grid: Array.from({ length: rows }, () => Array.from({ length: cols }, () => ({ ...blank }))),
        row: 0, col: 0, top: 0, bottom: rows - 1, alternate: false, originMode: false, history: [] });
}
export function resizeActionProof(rows, cols, runId = 'a'.repeat(32)) {
    return { paneId: editingPaneId, runId, observedAt: '2026-11-03T17:00:00.000Z', app: { rows, cols, layoutSettled: true, exact: true },
        terminalOutput: { tool: '/bin/stty', outputProcessing: false, description: `${rows} rows; ${cols} columns;\n-opost` } };
}

import { makeResizeCases } from '../resize-cases.mjs';
import { screenSnapshot, snapshotHash } from '../agent-observer.mjs';
export function syntheticResizeJournal(runId = 'a'.repeat(32)) {
    const frames = [], actions = [], original = initialResizeScreen();
    const add = (kind, raw, caseId = null, stageId = null, actionIndex = null) => {
        const screen = screenSnapshot(raw, false, true, true, true, true);
        const sha256 = snapshotHash(screen);
        frames.push({ index: frames.length, kind: 'resize-' + kind, paneId: editingPaneId,
            barrier: kind === 'start' ? null : '\x1b[?1;2c', screen, sha256,
            afterScreen: structuredClone(screen), afterSha256: sha256, caseId, stageId, actionIndex });
    };
    add('start', original);
    const bound = structuredClone(original); bound.lines[0] = 'NTILDE_RZ_' + runId;
    bound.cursorCol = 42; bound.bufferState.alternateScreen = true; add('bind', bound);
    for (const item of makeResizeCases(original.rows, original.cols)) {
        add('read', resizeWire(item.before), item.id, 'control');
        for (const stageId of ['after', 'restored']) {
            const target = stageId === 'after' ? item.target : { rows: original.rows, cols: original.cols };
            const index = actions.length;
            actions.push({ index, caseId: item.id, stageId, target, observed: resizeActionProof(target.rows, target.cols, runId) });
            add('read', resizeWire(item[stageId]), item.id, stageId, index);
        }
    }
    add('finish', original);
    return { frames, actions };
}
