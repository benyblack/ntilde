// Synthetic records only; never runtime qualification or evidence of host delivery.
import { initialEditingScreen, editingPaneId } from './editing-journal.mjs';
import { makeMetadataCases, metadataFixture } from '../metadata-cases.mjs';
import { expectedMetadataScreen } from '../metadata-expectations.mjs';
import { copyPaneMetadata, copyMetadataStatus } from '../metadata-state.mjs';
import { metadataProjection } from '../metadata.mjs';
import { screenSnapshot, snapshotHash } from '../agent-observer.mjs';
export { editingPaneId as metadataPaneId };
function metadataBuffer(screen, expected) {
    const mark = (row, col) => ({ row, col, absoluteRow: row, generation: 4, alternateScreen: false });
    return { rows: screen.rows, cols: screen.cols, cursorRow: screen.cursorRow, cursorCol: screen.cursorCol,
        alternateScreen: false, scrollbackLines: 0, historyGeneration: 4, rowsEvicted: 0, acceptingCommandInput: expected.type === 'shell-b',
        commandStart: ['shell-b', 'shell-c'].includes(expected.type) ? mark(expected.row, expected.col) : null,
        outputStart: expected.type === 'shell-c' ? mark(expected.row + 1, 0) : null,
        links: expected.type === 'link' ? [{ row: expected.row, col: expected.col, length: 4, identity: 0, uri: expected.uri, id: expected.id }] : [] };
}
function metadataPresentation(title, expected, runId, focused) {
    const progress = expected.type === 'progress', toast = ['notification', 'notification-hidden'].includes(expected.type) && !focused;
    const suffix = progress ? { Normal: ' 42%', Error: ' ✖ 42%', Paused: ' ⏸ 42%', Indeterminate: ' ⋯' }[expected.kind] : '';
    return { windowActive: focused, windowVisible: true, paneActive: true, focusMode: false,
        notificationsEnabled: true, progressEnabled: true, tabTextVisible: true, tabText: title + suffix,
        progressOwner: progress ? editingPaneId : null, progressKind: progress ? expected.kind : null,
        progressPercent: progress ? expected.percent : null, progressBar: null,
        toastVisible: expected.type === 'notification' && !focused,
        toastTitle: toast ? metadataFixture(runId).title : '', toastMessage: toast ? expected.message : '' };
}
export function metadataWire(screen, expected, runId, { unprepared = false, focused = false } = {}) {
    const f = metadataFixture(runId);
    let title = expected.type === 'title' && expected.title ? expected.title : f.title;
    let cwd = expected.type === 'cwd' ? expected.cwd : f.cwd;
    if (unprepared) { title = 'Owned fixture'; cwd = null; }
    return { paneId: editingPaneId, oscTitle: unprepared ? null : title, workingDirectory: cwd, displayTitle: title,
        buffer: metadataBuffer(screen, expected), presentation: metadataPresentation(title, expected, runId, focused) };
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
