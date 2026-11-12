// Synthetic unit-only state, never runtime observations.
import { editingWire, editingPaneId } from './editing-journal.mjs';
import { makeReflowCases, asciiReflowFinalRestore, asciiReflowNoncePrefix, asciiReflowSuiteId, asciiReflowCaseIds } from '../ascii-reflow-contracts.mjs';
import { screenSnapshot, snapshotHash } from '../agent-observer.mjs';
import { replayReflowPinned } from '../ascii-reflow-grading.mjs';
import { asciiReflowAdapterId } from '../ascii-reflow-grading-collector.mjs';

export const reflowPaneId = editingPaneId;
export function reflowWire(state) {
    return { ...editingWire(state), rows: state.grid.length, cols: state.grid[0].length };
}
export function initialReflowScreen(rows = 40, cols = 140) {
    const blank = { text: ' ', overline: false, flags: 768, fg: -1, bg: -1,
        foregroundArgb: null, backgroundArgb: null, underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null };
    return reflowWire({ grid: Array.from({ length: rows }, () => Array.from({ length: cols }, () => ({ ...blank }))),
        row: 0, col: 0, top: 0, bottom: rows - 1, alternate: false, originMode: false, history: [] });
}
export function reflowActionProof(rows, cols, runId = 'a'.repeat(32)) {
    return { paneId: editingPaneId, runId, observedAt: '2026-11-12T17:00:00.000Z', app: { rows, cols, layoutSettled: true, exact: true },
        terminalOutput: { tool: '/bin/stty', outputProcessing: false, description: `${rows} rows; ${cols} columns;\n-opost` } };
}

/** Build coherent unit-only records for refusal tests; these are never runtime measurements. */
export function syntheticReflowJournal(runId = 'a'.repeat(32), geometry = {}) {
    const frames = [], actions = [];
    const original = initialReflowScreen(geometry.rows, geometry.cols);
    const add = (kind, raw, caseId = null, stageId = null, actionIndex = null) => {
        const screen = screenSnapshot(raw, false, true, true, true, true);
        const sha256 = snapshotHash(screen);
        frames.push({ index: frames.length, kind: 'reflow-' + kind, paneId: editingPaneId,
            barrier: kind === 'start' ? null : '\x1b[?1;2c', screen, sha256,
            afterScreen: structuredClone(screen), afterSha256: sha256, caseId, stageId, actionIndex });
    };
    add('start', original);
    const bound = structuredClone(original);
    bound.lines[0] = asciiReflowNoncePrefix + runId;
    bound.cursorCol = (asciiReflowNoncePrefix + runId).length; bound.bufferState.alternateScreen = true;
    add('bind', bound);
    const cases = makeReflowCases(original.rows, original.cols);
    const feeds = [], barriers = [], stages = [], reads = [];
    const record = bytes => { feeds.push(bytes); barriers.push('\x1b[?1;2c'); };
    for (const item of cases) {
        stages.push({ caseId: item.id, stageId: 'control' });
        record(item.setup);
        add('read', reflowWire(item.before), item.id, 'control');
        reads.push({ cursor: [frames.at(-1).screen.cursorRow, frames.at(-1).screen.cursorCol] });
        for (const stageId of ['after', 'restored']) {
            const target = stageId === 'after' ? item.target : { rows: original.rows, cols: original.cols };
            const index = actions.length;
            actions.push({ index, caseId: item.id, stageId, target, observed: reflowActionProof(target.rows, target.cols, runId) });
            stages.push({ caseId: item.id, stageId });
            add('read', reflowWire(item[stageId]), item.id, stageId, index);
            reads.push({ cursor: [frames.at(-1).screen.cursorRow, frames.at(-1).screen.cursorCol] });
        }
    }
    record(asciiReflowFinalRestore);
    add('finish', original);
    const run = { schemaVersion: 1, suiteId: asciiReflowSuiteId, runId, adapterId: asciiReflowAdapterId, probeIds: [...asciiReflowCaseIds],
        feeds, barriers, stages, reads,
        pinned: { probeId: 'extensions.reflow', ...replayReflowPinned() } };
    return { frames, actions, run, runId, cases,
        adapter: { id: asciiReflowAdapterId, derivedFromAdapterId: 'ntilde-owned-osc-title-grading-v1',
            bundleSha256: 'f'.repeat(64), upstreamBundleSha256: 'a'.repeat(64) } };
}
