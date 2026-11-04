// Synthetic examples only, never collected child bytes or App provenance.
import { initialResizeScreen, resizePaneId } from './resize-journal.mjs';
import { screenSnapshot, snapshotHash } from '../agent-observer.mjs';
import { makeInputCases } from '../input-cases.mjs';
import { boundInputScreen, defaultInputModes, inputModeReplyHex } from '../input-state.mjs';
export const inputPaneId = resizePaneId, inputRunId = 'a'.repeat(32);
export const initialInputScreen = (rows = 12, cols = 120) => screenSnapshot(initialResizeScreen(rows, cols), false, true, true, true, true);
export function inputCapture(hex, index) {
    const nonces = [32768 + index * 2, 32769 + index * 2];
    return { hex, byteLength: hex.length / 2, nonces, barrier: nonces.map(n => `\x1b[?${n};0$y`).join('') };
}
export function inputActionProof(event, modes, runId = inputRunId, paneId = inputPaneId, rows = 12, cols = 120) {
    const pane = { paneId, rows, cols, kind: 'local', profileName: 'fixture', windowless: false };
    const side = { pane, app: { pid: 42, ppid: 41, uid: 1000, tty: '??', executable: '/owned/app' },
        worker: { pid: 43, ppid: 42, uid: 1000, tty: 'ttys100', executable: '/node' },
        appCommand: `/owned/app --ntilde-terminfo-owner=${runId}`, workerCommand: `/node --ntilde-terminfo-worker=${runId}`,
        terminalOutput: { tool: '/bin/stty', outputProcessing: false, description: `${rows} rows; ${cols} columns;\n-opost` } };
    const pointer = event.kind.startsWith('mouse');
    return { paneId, runId, request: { paneId, ...event }, rpcStarted: true, postVerified: true,
        observedAt: '2026-11-04T12:00:00.000Z', before: structuredClone(side), after: structuredClone(side),
        app: { eventDispatched: true, inputSubmitted: Boolean(event.kind !== 'keyUp'), handled: true,
            observationAvailable: true, boundary: 'avalonia-routed', rows, cols, viewRows: rows, viewCols: cols,
            modifiers: event.modifiers, ...modes, column: pointer ? event.column : null, row: pointer ? event.row : null,
            x: pointer ? (event.column - 0.5) * 10 : null, y: pointer ? (event.row - 0.5) * 10 : null } };
}
export function syntheticInputJournal(runId = inputRunId, { paneId = inputPaneId, initial = initialInputScreen() } = {}) {
    const bound = boundInputScreen(initial, runId), frames = [], actions = [];
    let attempt = 0;
    const add = (kind, screen, capture = null, caseId = null, actionIndex = null) => frames.push({ index: frames.length,
        kind: 'input-' + kind, paneId, screen: structuredClone(screen), afterScreen: structuredClone(screen),
        sha256: snapshotHash(screen), afterSha256: snapshotHash(screen), caseId, actionIndex, capture });
    add('start', initial); add('bind', bound, inputCapture('', attempt++));
    for (const item of makeInputCases(initial.rows, initial.cols)) {
        add('control', bound, inputCapture(inputModeReplyHex(item.modes), attempt++), item.id);
        actions.push({ index: actions.length, caseId: item.id, observed: inputActionProof(item.event, item.modes, runId, paneId, initial.rows, initial.cols) });
        add('read', bound, inputCapture(item.expectedHex, attempt++), item.id, actions.length - 1);
    }
    const capture = inputCapture(inputModeReplyHex(defaultInputModes), attempt++); add('finish', initial, capture);
    const seal = { failed: false, disposed: true, attempts: attempt, lastAttempt: { completed: true, fenceRequested: true,
        hex: capture.hex + Buffer.from(capture.barrier).toString('hex'), nonces: [...capture.nonces], error: null } };
    return { frames, actions, seal };
}
