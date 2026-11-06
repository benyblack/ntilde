// Synthetic, fully valid SGR-colors grading evidence for portable tests. Every
// frame, run-log entry and pinned result is produced through the same
// production validators used for real measurements; nothing here replaces the
// frozen real-App publication.
import { snapshotHash } from '../agent-observer.mjs';
import { sgrColorsContracts, sgrColorsReset, sgrColorsFeeds, sgrColorsCellAt,
    gradeSgrColorsCase } from '../sgr-colors-contracts.mjs';
import { replaySgrColorsPinned } from '../sgr-colors-grading.mjs';
import { sgrColorsAdapterId } from '../sgr-colors-grading-collector.mjs';

export const sgrColorsRows = 6;
export const sgrColorsCols = 60;
const cells = sgrColorsRows * sgrColorsCols;
const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';
const plain = { flags: 768, fg: -1, bg: -1 };

function screen(runs) {
    return { rows: sgrColorsRows, cols: sgrColorsCols, lines: ['CXY', '', '', '', '', ''],
        cursorRow: 0, cursorCol: 3, cursorVisible: true, attributeRuns: runs };
}

function frame(index, kind, screenValue, barrier) {
    return { index, kind, paneId, barrier, screen: screenValue, sha256: snapshotHash(screenValue) };
}

/** Attribute runs for the three contracted cells plus the plain remainder,
 * coalesced so adjacent identical runs stay canonical. */
function runs([first, second, third]) {
    const all = [{ count: 1, ...first }, { count: 1, ...second }, { count: 1, ...third },
        { count: cells - 3, ...plain }];
    const coalesced = [];
    for (const run of all) {
        const previous = coalesced.at(-1);
        if (previous && previous.flags === run.flags && previous.fg === run.fg && previous.bg === run.bg) previous.count += run.count;
        else coalesced.push(run);
    }
    return coalesced;
}

/** Observed attribute tuples per case (palette-fg 0x4000, palette-bg 0x8000). */
export function syntheticSgrColorsScreen(caseId) {
    switch (caseId) {
        case 'sgr.selective-reset.bold':
            // C plain; X bold+faint+italic; Y italic only (22 cleared bold+faint).
            return screen(runs([plain, { flags: 768 | 0x41 | 0x04, fg: -1, bg: -1 }, { flags: 768 | 0x04, fg: -1, bg: -1 }]));
        case 'sgr.selective-reset.italic':
            // C plain; X bold+italic; Y bold only (23 cleared italic).
            return screen(runs([plain, { flags: 768 | 0x01 | 0x04, fg: -1, bg: -1 }, { flags: 768 | 0x01, fg: -1, bg: -1 }]));
        case 'sgr.reset':
            // C plain; X bold+italic+underline; Y plain again after SGR 0.
            return screen(runs([plain, { flags: 768 | 0x01 | 0x04 | 0x08, fg: -1, bg: -1 }, plain]));
        case 'sgr.fg.standard':
            // C plain; X fg palette index 1 (31); Y fg palette index 4 (34); bg default.
            return screen(runs([plain,
                { flags: 0x0200 | 0x4000, fg: 1, bg: -1 }, { flags: 0x0200 | 0x4000, fg: 4, bg: -1 }]));
        case 'sgr.bg.standard':
            // C plain; X bg palette index 1 (41); Y bg palette index 4 (44); fg default.
            return screen(runs([plain,
                { flags: 0x0100 | 0x8000, fg: -1, bg: 1 }, { flags: 0x0100 | 0x8000, fg: -1, bg: 4 }]));
        default: throw new Error('Unknown synthetic SGR-colors case ' + caseId);
    }
}

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export function syntheticSgrColorsEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64)) {
    const nonce = 'NTILDE_SC_' + runId;
    const blank = { rows: sgrColorsRows, cols: sgrColorsCols, lines: ['', '', '', '', '', ''],
        cursorRow: 0, cursorCol: 0, cursorVisible: true,
        attributeRuns: [{ count: cells, ...plain }] };
    const bindScreen = { ...blank, lines: [nonce, '', '', '', '', ''], cursorCol: nonce.length };
    const frames = [frame(0, 'sgcolors-start', blank, null),
        frame(1, 'sgcolors-bind', bindScreen, deviceAttribute)];
    const cases = sgrColorsContracts.map(contract => {
        const feeds = sgrColorsFeeds(contract);
        const barriers = feeds.map(() => deviceAttribute);
        const screens = [syntheticSgrColorsScreen(contract.caseId)];
        const readScreen = screens[0];
        frames.push(frame(frames.length, 'sgcolors-read', readScreen, deviceAttribute));
        const pinned = { probeId: contract.caseId, ...replaySgrColorsPinned(contract, screens) };
        return { caseId: contract.caseId, feeds, barriers,
            reads: [{ kind: 'cells', cursor: [readScreen.cursorRow, readScreen.cursorCol] }], pinned };
    });
    frames.push(frame(frames.length, 'sgcolors-finish', blank, deviceAttribute));
    const run = { schemaVersion: 1, adapterId: sgrColorsAdapterId,
        probeIds: sgrColorsContracts.map(contract => contract.caseId), cases };
    const adapter = { id: sgrColorsAdapterId, derivedFromAdapterId: 'ntilde-owned-ascii-sgr-grading-v1',
        bundleSha256, upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, reset: sgrColorsReset };
}

/** Graded cells of one synthetic case for direct assertions. */
export function syntheticSgrColorsCells(caseId) {
    const screenValue = syntheticSgrColorsScreen(caseId);
    return [0, 1, 2].map(col => sgrColorsCellAt(screenValue, 0, col));
}

/** The independent contract verdict for one synthetic case. */
export function syntheticSgrColorsGrade(caseId) {
    const contract = sgrColorsContracts.find(item => item.caseId === caseId);
    return gradeSgrColorsCase(contract, syntheticSgrColorsCells(caseId));
}
