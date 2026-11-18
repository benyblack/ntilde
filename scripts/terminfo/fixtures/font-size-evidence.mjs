// Synthetic evidence only. Literal reply bytes are a second oracle; no App or
// terminal ownership claim. The measured contract oracle does not generate them.
import { syntheticDevqEvidence } from './devq-evidence.mjs';
import { snapshotHash } from '../agent-observer.mjs';
import { fontSizeContracts, fontSizeNonce, replayFontSizePinned } from '../font-size-contracts.mjs';
import { fontSizeAdapterId } from '../font-size-grading-collector.mjs';

export const fontSizeSyntheticReply = item => {
    if (item.protocol === 'csi') return ['\x1b[6;35;17t'];
    if (item.protocol === '7770') return ['\x1b]7770;12' + item.terminator];
    if (item.protocol === '7777') return ['\x1b]7777;12' + item.terminator];
    return [];
};
export function syntheticFontSizeEvidence(mutateReply = null) {
    const source = syntheticDevqEvidence(), runId = source.runId;
    const main = source.frames[0].screen, bound = structuredClone(source.frames[1].screen);
    bound.lines[0] = fontSizeNonce + runId;
    bound.cursorCol = bound.lines[0].length;
    const geometry = { viewRows: main.rows, viewCols: main.cols, cellWidthDip: 8.5, cellHeightDip: 17.5, fontAscentDip: 12.5, renderScaling: 2, backingScale: 2, fontSizeDip: 16, windowWidthDip: 1200, windowHeightDip: 800 };
    const da = '\x1b[?62;4;22c', paneId = source.frames[0].paneId;
    const frames = [];
    const add = (kind, screen, barrier, report) => {
        frames.push({ index: frames.length, kind: 'fnt-' + kind, paneId, barrier,
            screen: structuredClone(screen), sha256: snapshotHash(screen), geometry: { ...geometry }, ...(report ? { report: structuredClone(report) } : {}) });
    };
    add('start', main, null); add('bind', bound, da);
    const cases = fontSizeContracts.map((contract, index) => {
        const exchanges = contract.exchanges.map(item => {
            const replies = mutateReply?.(contract.caseId, item, fontSizeSyntheticReply(item)) ?? fontSizeSyntheticReply(item);
            const report = { replies, barrier: da };
            add('read', bound, da, report);
            return { id: item.id, request: item.request, report };
        });
        const report = exchanges.find(item => item.id === 'pinned').report;
        const pinned = Object.fromEntries(['termless', 'terminal'].map(path => [path,
            { probeId: contract.caseId, ...replayFontSizePinned(index, report.replies.join('') + (path === 'terminal' ? da : '')) }]));
        return { caseId: contract.caseId, exchanges, pinned };
    });
    add('finish', main, da);
    return { frames, run: { schemaVersion: 1, runId, cases }, runId,
        adapter: { id: fontSizeAdapterId, bundleSha256: 'a'.repeat(64), upstreamBundleSha256: 'b'.repeat(64) } };
}
