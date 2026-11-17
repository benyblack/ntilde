// Synthetic evidence only. Literal reply bytes are a second oracle; no App or
// terminal ownership claim. The measured contract oracle does not generate them.
import { syntheticDevqEvidence } from './devq-evidence.mjs';
import { snapshotHash } from '../agent-observer.mjs';
import { cellSizeContracts, cellSizeNonce, replayCellSizePinned } from '../cell-size-contracts.mjs';
import { cellSizeAdapterId } from '../cell-size-grading-collector.mjs';

export const cellSizeSyntheticReply = item => {
    if (item.protocol === 'csi') return ['\x1b[6;35;17t'];
    if (item.protocol === 'iterm') return ['\x1b]1337;ReportCellSize=17.5;8.5;2\x1b\\'];
    if (item.protocol === 'urxvt') return ['\x1b]776;17;35;25' + item.terminator];
    return [];
};
export function syntheticCellSizeEvidence(mutateReply = null) {
    const source = syntheticDevqEvidence(), runId = source.runId;
    const main = source.frames[0].screen, bound = structuredClone(source.frames[1].screen);
    bound.lines[0] = cellSizeNonce + runId;
    bound.cursorCol = bound.lines[0].length;
    const geometry = { viewRows: main.rows, viewCols: main.cols, cellWidthDip: 8.5, cellHeightDip: 17.5, fontAscentDip: 12.5, renderScaling: 2, backingScale: 2 };
    const da = '\x1b[?62;4;22c', paneId = source.frames[0].paneId;
    const frames = [];
    const add = (kind, screen, barrier, report) => {
        frames.push({ index: frames.length, kind: 'csz-' + kind, paneId, barrier,
            screen: structuredClone(screen), sha256: snapshotHash(screen), geometry: { ...geometry }, ...(report ? { report: structuredClone(report) } : {}) });
    };
    add('start', main, null); add('bind', bound, da);
    const cases = cellSizeContracts.map((contract, index) => {
        const exchanges = contract.exchanges.map(item => {
            const replies = mutateReply?.(contract.caseId, item, cellSizeSyntheticReply(item)) ?? cellSizeSyntheticReply(item);
            const report = { replies, barrier: da };
            add('read', bound, da, report);
            return { id: item.id, request: item.request, report };
        });
        const report = exchanges.find(item => item.id === 'pinned').report;
        const pinned = Object.fromEntries(['termless', 'terminal'].map(path => [path,
            { probeId: contract.caseId, ...replayCellSizePinned(index, report.replies.join('') + (path === 'terminal' ? da : '')) }]));
        return { caseId: contract.caseId, exchanges, pinned };
    });
    add('finish', main, da);
    return { frames, run: { schemaVersion: 1, runId, cases }, runId,
        adapter: { id: cellSizeAdapterId, bundleSha256: 'a'.repeat(64), upstreamBundleSha256: 'b'.repeat(64) } };
}
