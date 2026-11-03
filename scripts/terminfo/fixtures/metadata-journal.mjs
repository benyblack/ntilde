// Synthetic records only; never runtime qualification or evidence of host delivery.
import { initialEditingScreen, editingPaneId } from './editing-journal.mjs';
import { makeMetadataCases, metadataFixture } from '../metadata-cases.mjs';
import { expectedMetadataScreen } from '../metadata-expectations.mjs';
import { copyPaneMetadata, copyMetadataStatus } from '../metadata-state.mjs';
import { metadataProjection } from '../metadata.mjs';
import { screenSnapshot, snapshotHash } from '../agent-observer.mjs';
export { editingPaneId as metadataPaneId };
export function metadataWire(screen, expected, runId, { unprepared = false, focused = false } = {}) {
    const f = metadataFixture(runId), shell = expected.type;
    const mark = (row, col) => ({ row, col, absoluteRow: row, generation: 4, alternateScreen: false });
    const title = expected.type === 'title' && expected.title ? expected.title : f.title;
    return { paneId: editingPaneId, oscTitle: unprepared ? null : title,
        workingDirectory: unprepared ? null : expected.type === 'cwd' ? expected.cwd : f.cwd,
        displayTitle: unprepared ? 'Owned fixture' : title,
        buffer: { rows: screen.rows, cols: screen.cols, cursorRow: screen.cursorRow, cursorCol: screen.cursorCol,
            alternateScreen: false, scrollbackLines: 0, historyGeneration: 4, rowsEvicted: 0, acceptingCommandInput: shell === 'shell-b',
            commandStart: ['shell-b', 'shell-c'].includes(shell) ? mark(expected.row, expected.col) : null,
            outputStart: shell === 'shell-c' ? mark(expected.row + 1, 0) : null,
            links: expected.type === 'link' ? [{ row: expected.row, col: expected.col, length: 4, identity: 0, uri: expected.uri, id: expected.id }] : [] },
        presentation: { windowActive: focused, windowVisible: true, paneActive: true, focusMode: false,
            notificationsEnabled: true, progressEnabled: true, tabTextVisible: true,
            tabText: (unprepared ? 'Owned fixture' : title) + (expected.type === 'progress'
                ? { Normal: ' 42%', Error: ' ✖ 42%', Paused: ' ⏸ 42%', Indeterminate: ' ⋯' }[expected.kind] : ''),
            progressOwner: expected.type === 'progress' ? editingPaneId : null, progressKind: expected.type === 'progress' ? expected.kind : null,
            progressPercent: expected.type === 'progress' ? expected.percent : null, progressBar: null,
            toastVisible: expected.type === 'notification' && !focused,
            toastTitle: ['notification', 'notification-hidden'].includes(expected.type) && !focused ? f.title : '',
            toastMessage: ['notification', 'notification-hidden'].includes(expected.type) && !focused ? expected.message : '' } };
}
export function metadataStatus(expected, { unprepared = false } = {}) {
    return { paneId: editingPaneId, status: expected.type === 'shell-c' ? 'running' : 'awaitingInput',
        confidence: unprepared ? 'heuristic' : 'precise', currentCommand: expected.type === 'shell-c' ? expected.command : null,
        exitCode: null, statusSinceMs: 100, lastOutputAtMs: 101, isStalled: false, stallThresholdSeconds: 30, idleThresholdSeconds: 2 };
}
export function syntheticMetadataJournal(runId = 'a'.repeat(32), { rows = 12, cols = 60, focused = false } = {}) {
    const initial = screenSnapshot(initialEditingScreen(rows, cols), false, true, true, true, true), frames = [];
    const add = (operation, expected, caseId = null, stageId = null, options = {}) => {
        const screen = expectedMetadataScreen(initial, expected, runId, operation === 'start' || operation === 'finish');
        const metadata = copyPaneMetadata(metadataWire(screen, expected, runId, { focused, ...options }));
        const rawStatus = metadataStatus(expected, options), status = copyMetadataStatus(rawStatus, editingPaneId);
        const before = { screen, metadata, status, rawStatus }, sha256 = snapshotHash(metadataProjection(before));
        frames.push({ index: frames.length, kind: 'metadata-' + operation, paneId: editingPaneId,
            barrier: operation === 'start' ? null : '\x1b[?1;2c', before, sha256, after: structuredClone(before), afterSha256: sha256, caseId, stageId });
    };
    add('start', { type: 'baseline' }, null, null, { unprepared: true });
    add('bind', { type: 'baseline' });
    for (const item of makeMetadataCases(rows, cols, runId)) for (const stage of item.stages) add('read', stage.expected, item.id, stage.id);
    add('finish', { type: 'baseline' }); return frames;
}
