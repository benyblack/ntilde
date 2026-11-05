// Synthetic, fully valid ASCII/SGR grading evidence for portable tests. Every
// frame, run-log entry and pinned result is produced through the same
// production validators used for real measurements; nothing here replaces the
// frozen real-App publication.
import { createHash } from 'node:crypto';
import { snapshotHash } from '../agent-observer.mjs';
import { asciiSgrContracts, asciiSgrReset, contractFeeds, gradeRecordedCase } from '../ascii-sgr-contracts.mjs';
import { gradingAdapterId } from '../grading-collector.mjs';

export const syntheticRows = 6;
export const syntheticCols = 60;
const cells = syntheticRows * syntheticCols;
const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';

function screen(lines, cursorRow, cursorCol, runs) {
    return { rows: syntheticRows, cols: syntheticCols, lines,
        cursorRow, cursorCol, cursorVisible: true,
        attributeRuns: runs ?? [{ count: cells, flags: 0, fg: -1, bg: -1 }] };
}

function frame(index, kind, screenValue, barrier) {
    return { index, kind, paneId, barrier, screen: screenValue, sha256: snapshotHash(screenValue) };
}

const blank = () => Array(syntheticRows).fill('');

/** Observed screen states per case, keyed by read index within the case. */
export function syntheticCaseScreens(caseId) {
    switch (caseId) {
        case 'text.basic':
            return [screen(blank(), 0, 0), screen(['Hello', ...blank().slice(1)], 0, 5)];
        case 'text.overwrite':
            // After CSI 1 G and printing C at column 0, the cursor sits at column 1.
            return [screen(['AB', ...blank().slice(1)], 0, 2), screen(['CB', ...blank().slice(1)], 0, 1)];
        case 'sgr.bold':
            return [screen(['X', ...blank().slice(1)], 0, 1,
                [{ count: 1, flags: 0x0001, fg: -1, bg: -1 }, { count: cells - 1, flags: 0, fg: -1, bg: -1 }])];
        case 'sgr.italic':
            return [screen(['X', ...blank().slice(1)], 0, 1,
                [{ count: 1, flags: 0x0004, fg: -1, bg: -1 }, { count: cells - 1, flags: 0, fg: -1, bg: -1 }])];
        default: throw new Error('Unknown synthetic case ' + caseId);
    }
}

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export function syntheticGradingEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64)) {
    const nonce = 'NTILDE_AG_' + runId;
    const frames = [frame(0, 'grading-start', screen(blank(), 0, 0), null),
        frame(1, 'grading-bind', screen([nonce, ...blank().slice(1)], 0, nonce.length), deviceAttribute)];
    const cases = asciiSgrContracts.map(contract => {
        const feeds = contractFeeds(contract);
        const barriers = feeds.map(() => deviceAttribute);
        const screens = syntheticCaseScreens(contract.caseId);
        const reads = [];
        for (const step of contract.plan.filter(item => item.read)) {
            const observed = screens[reads.length];
            frames.push(frame(frames.length, 'grading-read', observed, deviceAttribute));
            reads.push({ kind: step.read, cursor: [observed.cursorRow, observed.cursorCol] });
        }
        const graded = gradeRecordedCase(contract, screens);
        // Response shapes mirror the pinned parserStateResult exactly: the
        // termless probes stringify their own observed state.
        const cell = (screenValue, row, col) => {
            const flags = screenValue.attributeRuns.find((run, index) => {
                const before = screenValue.attributeRuns.slice(0, index).reduce((sum, item) => sum + item.count, 0);
                return before <= row * screenValue.cols + col && before + run.count > row * screenValue.cols + col;
            })?.flags ?? 0;
            return { char: (screenValue.lines[row] ?? ' ')[col] ?? ' ', flags,
                bold: (flags & 0x0001) !== 0, italic: (flags & 0x0004) !== 0 };
        };
        const text = contract.caseId === 'text.basic'
            ? JSON.stringify({ before: screens[0].lines.join('\n'), after: screens[1].lines.join('\n') })
            : contract.caseId === 'text.overwrite'
                ? JSON.stringify({ before: [cell(screens[0], 0, 0), cell(screens[0], 0, 1)],
                    first: cell(screens[1], 0, 0), second: cell(screens[1], 0, 1) })
                : JSON.stringify(cell(screens[0], 0, 0));
        const expected = contract.caseId.startsWith('sgr.')
            ? `${contract.caseId}: attribute applied to the rendered X cell`
            : contract.pinnedCriterion;
        const pinned = { probeId: contract.probeId, pass: graded.outcome === 'supported',
            response: text, ...(graded.note ? { note: graded.note } : {}),
            observation: { outcome: graded.outcome, evidence: 'parser-state',
                ...(graded.outcome === 'inconclusive' ? { reason: 'insufficient-evidence' } : {}),
                ...(graded.note ? { note: graded.note } : {}) },
            ...(graded.outcome !== 'inconclusive' ? { assertions: [{ kind: graded.outcome === 'supported' ? 'positive' : 'negative',
                expected, observed: text }] } : {}) };
        return { caseId: contract.caseId, feeds, barriers, reads, pinned };
    });
    frames.push(frame(frames.length, 'grading-finish', screen(blank(), 0, 0), deviceAttribute));
    const run = { schemaVersion: 1, adapterId: gradingAdapterId,
        probeIds: asciiSgrContracts.map(contract => contract.caseId), cases };
    const adapter = { id: gradingAdapterId, derivedFromAdapterId: 'ntilde-owned-macos-v1',
        bundleSha256, upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, reset: asciiSgrReset };
}
