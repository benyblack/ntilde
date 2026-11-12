import { snapshotHash } from '../agent-observer.mjs';
import { insertReplaceContract, insertReplaceFeeds, insertReplaceNoncePrefix } from '../insert-replace-contracts.mjs';
import { insertReplaceAdapterId } from '../insert-replace-grading-collector.mjs';
import { lrMarginsAdapterId } from '../lr-margins-grading-collector.mjs';
import { replayInsertReplacePinned, replayInsertReplaceTerminal } from '../insert-replace-pinned-replay.mjs';
import { buildInsertReplaceReport } from '../insert-replace-grading.mjs';
import { syntheticLineEditScreen, rowOf } from './line-edit-evidence.mjs';

export const runId = 'a'.repeat(32), paneId = '0f0e0d0c-0b0a-4009-8008-700600500400', barrier = '\x1b[?1;2c';
const screen = (text = '', col = 0, insert = false, alternate = true) => {
    const value = syntheticLineEditScreen([rowOf(text)], [0, col], { insert });
    value.bufferState.autoWrap = false;
    value.bufferState.alternateScreen = alternate;
    value.horizontalMargins = { leftRightMarginMode: false, leftMargin: 0, rightMargin: value.cols - 1 };
    return value;
};
const frame = (index, kind, value) => ({ index, kind, paneId, barrier: index === 0 ? null : barrier, screen: value, sha256: snapshotHash(value) });
export function evidence() {
    const initial = screen('', 0, false, false), nonce = insertReplaceNoncePrefix + runId;
    const reads = insertReplaceContract.roles.map(role => role === 'seed' ? screen('ABC', 3)
        : role === 'home' ? screen('ABC', 0) : role === 'enabled' ? screen('ABC', 0, true)
            : role === 'inserted' ? screen('XABC', 1, true) : role === 'terminal' ? screen('AXBCD', 2) : screen());
    const frames = [frame(0, 'irm-start', initial), frame(1, 'irm-bind', screen(nonce, nonce.length)),
        ...reads.map((value, index) => frame(index + 2, 'irm-read', value)), frame(reads.length + 2, 'irm-finish', initial)];
    const pinned = replayInsertReplacePinned({ cols: initial.cols, rows: initial.rows, seed: ['A', 'B', 'C', ' '],
        origin: { x: 0, y: 0, visible: true }, after: ['X', 'A', 'B', 'C'] });
    const terminal = replayInsertReplaceTerminal({ cols: initial.cols, rows: initial.rows, position: { row: 1, col: 3 } });
    const run = { schemaVersion: 1, adapterId: insertReplaceAdapterId, probeIds: ['modes.insert-replace'], original: { insertMode: false, autoWrap: false },
        feeds: insertReplaceFeeds(), barriers: insertReplaceFeeds().map(() => barrier),
        reads: reads.map(value => ({ kind: 'cells', cursor: [value.cursorRow, value.cursorCol] })),
        pinned: { probeId: 'modes.insert-replace', ...pinned }, terminalPinned: { probeId: 'modes.insert-replace', ...terminal },
        terminalCursorEvidence: 'parent-observed cursor projection; not a PTY cursor reply' };
    const adapter = { id: insertReplaceAdapterId, derivedFromAdapterId: lrMarginsAdapterId, bundleSha256: 'f'.repeat(64), upstreamBundleSha256: 'e'.repeat(64) };
    return { frames, run, adapter };
}
