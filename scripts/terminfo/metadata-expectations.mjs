import { snapshotHash } from './agent-observer.mjs';
import { metadataFixture } from './metadata-cases.mjs';
const same = (a, b) => snapshotHash(a) === snapshotHash(b);
/** All cells and stored attributes are compared, including unchanged cells outside the witness. */
export function expectedMetadataScreen(first, expected, runId, finish = false) {
    const screen = structuredClone(first), fixture = metadataFixture(runId);
    if (finish) return screen;
    screen.lines[0] = fixture.nonce; screen.cursorCol = fixture.nonce.length;
    if (['link', 'shell-b', 'shell-c', 'shell-d'].includes(expected.type)) {
        screen.lines[expected.row] = ' '.repeat(expected.col) + expected.text;
        screen.cursorRow = expected.row; screen.cursorCol = expected.col + expected.text.length;
    }
    return screen;
}
function anchor(value, row, col, buffer) {
    return value?.row === row && value.col === col && value.absoluteRow === row
        && value.generation === buffer.historyGeneration && value.alternateScreen === false;
}
function shellMatches(snapshot, expected) {
    const b = snapshot.metadata.buffer, s = snapshot.status;
    if (s.confidence !== 'precise') return false;
    if (expected.type === 'shell-c') return s.status === 'running' && s.currentCommand === expected.command
        && !b.acceptingCommandInput && anchor(b.commandStart, expected.row, expected.col, b)
        && anchor(b.outputStart, expected.row + 1, 0, b);
    if (!['awaitingInput', 'idle'].includes(s.status) || s.currentCommand !== null || b.outputStart !== null) return false;
    if (expected.type === 'shell-b') return b.acceptingCommandInput && anchor(b.commandStart, expected.row, expected.col, b);
    return !b.acceptingCommandInput && b.commandStart === null;
}
function progressMatches(snapshot, expected) {
    const p = snapshot.metadata.presentation;
    if (expected.type !== 'progress') return p.progressOwner === null && p.progressKind === null && p.progressPercent === null
        && !p.tabText.includes('%') && !p.tabText.includes('⋯') && !p.tabText.includes('⏸') && !p.tabText.includes('✖');
    const suffix = { Normal: ' 42%', Error: ' ✖ 42%', Paused: ' ⏸ 42%', Indeterminate: ' ⋯' }[expected.kind];
    return p.progressOwner === snapshot.metadata.paneId && p.progressKind === expected.kind
        && p.progressPercent === expected.percent && p.tabText.endsWith(suffix);
}
function toastMatches(snapshot, expected, runId) {
    const p = snapshot.metadata.presentation;
    if (expected.type === 'notification') return p.toastVisible && p.toastTitle === metadataFixture(runId).title && p.toastMessage === expected.message;
    if (expected.type === 'notification-hidden') return !p.toastVisible && p.toastTitle === metadataFixture(runId).title && p.toastMessage === expected.message;
    return !p.toastVisible;
}
/** Compare source-known host effects; no marker, cursor advance or parser acknowledgement counts alone. */
export function matchesMetadataExpected(snapshot, expected, first, runId, finish = false) {
    const m = snapshot.metadata, b = m.buffer, p = m.presentation, fixture = metadataFixture(runId);
    const title = expected.type === 'title' ? expected.title : fixture.title;
    const cwd = expected.type === 'cwd' ? expected.cwd : fixture.cwd;
    const expectedLinks = expected.type === 'link' ? [{ row: expected.row, col: expected.col, length: 4, identity: 0,
        uri: expected.uri, id: expected.id }] : [];
    return same(snapshot.screen, expectedMetadataScreen(first, expected, runId, finish))
        && m.oscTitle === title && m.displayTitle === title && (title === '' ? p.tabText === '' : p.tabText.includes(title)) && m.workingDirectory === cwd
        && b.alternateScreen === false && b.scrollbackLines === 0 && b.rowsEvicted === 0
        && same(b.links, expectedLinks) && shellMatches(snapshot, expected) && progressMatches(snapshot, expected)
        && toastMatches(snapshot, expected, runId) && p.windowVisible && p.paneActive && !p.focusMode
        && p.notificationsEnabled && p.progressEnabled && p.tabTextVisible && p.progressBar === null;
}
