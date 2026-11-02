import { screenSnapshot, snapshotHash } from './agent-observer.mjs';

export const cellSuiteId = 'ntilde-owned-ascii-cells-v1';
const reset = '\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H';
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const view = (first, row, col) => ({ first, row, col });

export const cellCases = Object.freeze([
    { id: 'text.basic', seed: '', cursor: [0, 0], steps: [['Hello', view('Hello', 0, 5)]] },
    { id: 'text.overwrite', seed: 'ABCDE', cursor: [0, 2], steps: [['Z', view('ABZDE', 0, 3)]] },
    { id: 'cursor.move.absolute', seed: 'ABCDE', cursor: [0, 0], steps: [
        [at(1, 3), view('ABCDE', 1, 3)], [at(0, 1), view('ABCDE', 0, 1)]] },
    { id: 'cursor.cup-scroll-region', seed: 'ABCDE', cursor: [0, 0], steps: [
        ['\x1b[2;3r\x1b[?6l' + at(0, 1), view('ABCDE', 0, 1)],
        ['\x1b[?6h' + at(0, 1), view('ABCDE', 1, 1)]] },
    { id: 'erase.line.right', seed: 'ABCDE', cursor: [0, 2], steps: [['\x1b[0K', view('AB', 0, 2)]] },
    { id: 'erase.line.left', seed: 'ABCDE', cursor: [0, 2], steps: [['\x1b[1K', view('   DE', 0, 2)]] },
    { id: 'erase.line.all', seed: 'ABCDE', cursor: [0, 2], steps: [['\x1b[2K', view('', 0, 2)]] },
    { id: 'modes.insert-replace', seed: 'ABCD', cursor: [0, 1], steps: [
        ['\x1b[4hX', view('AXBCD', 0, 2)], ['\x1b[4lY', view('AXYCD', 0, 3)]] },
]);

export function matchesCells(screen, expected) {
    return screen.cursorRow === expected.row && screen.cursorCol === expected.col
        && screen.lines.every((line, row) => line === (row === 0 ? expected.first : row === 1 ? 'KEEP!' : ''));
}

export async function collectCells({ config, owner, fence }) {
    await owner('cell-start');
    try {
        const barrier = await fence.write('\x1b[?1049h' + reset + config.cellNonce);
        await owner('cell-bind', barrier);
        for (const fixture of cellCases) {
            const setup = reset + at(1, 0) + 'KEEP!' + at(0, 0) + fixture.seed + at(...fixture.cursor);
            await owner('cell-read', await fence.write(setup));
            for (const [sequence] of fixture.steps) await owner('cell-read', await fence.write(sequence));
        }
    } finally {
        await owner('cell-finish', await fence.write(reset + '\x1b[?1049l'));
    }
}

function validateFrame(frame, index, baseline) {
    screenSnapshot(frame.screen);
    if (frame.index !== index || frame.sha256 !== snapshotHash(frame.screen) || frame.paneId !== baseline.paneId
        || frame.screen.rows !== baseline.screen.rows || frame.screen.cols !== baseline.screen.cols) {
        throw new Error('Changed owned cell snapshot evidence.');
    }
    if (index === 0 ? frame.barrier !== null : !/^\x1b\[\?[\d;]+c$/.test(frame.barrier ?? '')) {
        throw new Error('Missing cell ordering barrier evidence.');
    }
}

function validateFrames(snapshots, runId) {
    const count = 3 + cellCases.reduce((sum, fixture) => sum + 1 + fixture.steps.length, 0);
    if (snapshots.length !== count || snapshots[0].kind !== 'cell-start' || snapshots[1].kind !== 'cell-bind'
        || snapshots.at(-1).kind !== 'cell-finish') throw new Error('Incomplete owned cell snapshot sequence.');
    for (const [index, frame] of snapshots.entries()) validateFrame(frame, index, snapshots[0]);
    const binding = snapshots[1].screen;
    if (!/^[a-f\d]{32}$/.test(runId) || binding.lines[0] !== `NTILDE_QA_${runId}`
        || binding.cursorRow !== 0 || binding.cursorCol !== `NTILDE_QA_${runId}`.length) {
        throw new Error('Cell evidence is not bound to this run.');
    }
    if (snapshots[0].sha256 !== snapshots.at(-1).sha256) throw new Error('Cell fixture was not restored.');
}

export function buildCellReport(snapshots, runId) {
    validateFrames(snapshots, runId);
    let offset = 2;
    const observations = cellCases.map(fixture => {
        const before = snapshots[offset++];
        const assertions = fixture.steps.map(([sequence, expected]) => {
            const target = snapshots[offset++];
            if (target.kind !== 'cell-read') throw new Error('Invalid cell experiment order.');
            return { sequence, expected, snapshot: target.index, passed: matchesCells(target.screen, expected) };
        });
        if (before.kind !== 'cell-read') throw new Error('Missing cell control.');
        const ready = matchesCells(before.screen, view(fixture.seed, ...fixture.cursor));
        return { featureId: fixture.id, before: before.index, assertions,
            result: !ready ? 'inconclusive' : assertions.every(a => a.passed) ? 'passed' : 'failed' };
    });
    return { schemaVersion: 1, suiteId: cellSuiteId, runId, complete: true,
        scope: 'Live App readScreen buffer projection: ASCII text and cursor state only; not pixels or stock collector grades.',
        initialSnapshot: 0, restoredSnapshot: snapshots.length - 1, snapshots, observations };
}
