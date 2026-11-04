import { snapshotHash, screenSnapshot } from './agent-observer.mjs';
import { inputSuiteId, makeInputCases } from './input-cases.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';
export const inputModeNumbers = Object.freeze([66, 9, 1000, 1002, 1003, 1006]);
export const inputModeQueries = inputModeNumbers.map(n => `\x1b[?${n}$p`).join('') + '\x1b[?u';
export const defaultInputModes = Object.freeze({ applicationKeypad: false, mouseTracking: 0, mouseSgr: false, kittyKeyboardFlags: 0 });
export const inputFinalRestore = '\x1b[?1049l\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[3J\x1b[H\x1b[?25h';
export const sameInputFact = (a, b) => snapshotHash(a) === snapshotHash(b);
export const checkInput = value => { if (!value) throw new Error('Invalid or incomplete owned routed-input evidence.'); };
export function boundInputScreen(initial, runId) {
    const bound = structuredClone(initial);
    bound.lines[0] = 'NTILDE_IN_' + runId; bound.cursorCol = bound.lines[0].length; bound.bufferState.alternateScreen = true;
    return bound;
}
export function inputModeReplyHex(modes) {
    return Buffer.from(inputModeNumbers.map(n => `\x1b[?${n};${(n === 66 ? modes.applicationKeypad : n === 1006 ? modes.mouseSgr : modes.mouseTracking === n) ? 1 : 2}$y`).join('')
        + `\x1b[?${modes.kittyKeyboardFlags}u`).toString('hex');
}
/** Raw hex is a copied child observation; challenge bytes are never event bytes. */
export function validateInputBytes(value, expectedHex, used) {
    checkInput(value && Object.keys(value).sort().join(',') === 'barrier,byteLength,hex,nonces'
        && typeof value.hex === 'string' && /^[a-f\d]*$/.test(value.hex) && value.hex.length % 2 === 0
        && value.byteLength === value.hex.length / 2 && value.byteLength <= 512 && value.hex === expectedHex
        && Array.isArray(value.nonces) && value.nonces.length === 2
        && value.nonces.every(n => Number.isSafeInteger(n) && n >= 32768 && n <= 65535) && value.nonces[0] !== value.nonces[1]
        && value.barrier === value.nonces.map(n => `\x1b[?${n};0$y`).join(''));
    const key = value.nonces.join(','); checkInput(!used.has(key)); used.add(key);
}
export function validateInputAction(action, item, paneId, runId, geometry) {
    checkInput(action?.paneId === paneId && action.runId === runId && action.rpcStarted === true && action.postVerified === true
        && !action.error && sameInputFact(action.request, { paneId, ...item.event })
        && typeof action.observedAt === 'string' && new Date(action.observedAt).toISOString() === action.observedAt);
    for (const side of ['before', 'after']) {
        const fact = action[side];
        checkInput(fact?.pane?.paneId === paneId && fact.pane.kind === 'local' && !fact.pane.windowless
            && fact.pane.rows === geometry.rows && fact.pane.cols === geometry.cols
            && fact.terminalOutput?.tool === '/bin/stty' && fact.terminalOutput.outputProcessing === false);
        validateOutputGeometry(fact.terminalOutput.description, geometry.rows, geometry.cols);
    }
    const app = action.app;
    checkInput(app?.eventDispatched === true && app.observationAvailable === true && app.boundary === 'avalonia-routed'
        && typeof app.inputSubmitted === 'boolean' && typeof app.handled === 'boolean'
        && app.modifiers === item.event.modifiers && app.rows === geometry.rows && app.cols === geometry.cols
        && app.viewRows === geometry.rows && app.viewCols === geometry.cols
        && Object.entries(item.modes).every(([key, value]) => app[key] === value));
    if (item.event.kind.startsWith('mouse')) checkInput(app.column === item.event.column && app.row === item.event.row
        && Number.isFinite(app.x) && app.x >= 0 && Number.isFinite(app.y) && app.y >= 0);
    else checkInput(['column', 'row', 'x', 'y'].every(key => app[key] == null));
}
export function validateInputFrame(frame, index, kind, paneId) {
    checkInput(frame?.index === index && frame.kind === kind && frame.paneId === paneId);
    screenSnapshot(frame.screen, false, true, true, true, true);
    screenSnapshot(frame.afterScreen, false, true, true, true, true);
    checkInput(frame.sha256 === snapshotHash(frame.screen) && frame.afterSha256 === snapshotHash(frame.afterScreen)
        && sameInputFact(frame.screen, frame.afterScreen));
}
export function buildInputReport(frames, actions, seal, runId) {
    checkInput(/^[a-f\d]{32}$/.test(runId ?? '') && frames?.length === 81 && actions?.length === 39);
    const paneId = frames[0].paneId, initial = frames[0].screen, geometry = { rows: initial.rows, cols: initial.cols };
    checkInput(pristineEditing(initial));
    const cases = makeInputCases(initial.rows, initial.cols), used = new Set();
    validateInputFrame(frames[0], 0, 'input-start', paneId);
    validateInputFrame(frames[1], 1, 'input-bind', paneId);
    const bound = boundInputScreen(initial, runId);
    checkInput(sameInputFact(frames[1].screen, bound)); validateInputBytes(frames[1].capture, '', used);
    const observations = cases.map((item, index) => {
        const control = frames[index * 2 + 2], observed = frames[index * 2 + 3];
        validateInputFrame(control, index * 2 + 2, 'input-control', paneId);
        validateInputFrame(observed, index * 2 + 3, 'input-read', paneId);
        checkInput(control.caseId === item.id && observed.caseId === item.id && observed.actionIndex === index
            && sameInputFact(control.screen, bound) && sameInputFact(observed.screen, bound)
            && actions[index].index === index && actions[index].caseId === item.id);
        validateInputBytes(control.capture, inputModeReplyHex(item.modes), used);
        validateInputBytes(observed.capture, item.expectedHex, used);
        validateInputAction(actions[index].observed, item, paneId, runId, geometry);
        return { featureId: item.id, result: 'passed', expectedHex: item.expectedHex, actualHex: observed.capture.hex,
            controlSnapshot: control.index, snapshot: observed.index, actionIndex: index };
    });
    const final = frames.at(-1); validateInputFrame(final, 80, 'input-finish', paneId);
    checkInput(sameInputFact(final.screen, initial)); validateInputBytes(final.capture, inputModeReplyHex(defaultInputModes), used);
    checkInput(seal?.failed === false && seal.disposed === true && seal.attempts === 80 && seal.lastAttempt?.completed === true
        && seal.lastAttempt.error === null && seal.lastAttempt.fenceRequested === true
        && seal.lastAttempt.hex === final.capture.hex + Buffer.from(final.capture.barrier).toString('hex')
        && sameInputFact(seal.lastAttempt.nonces, final.capture.nonces));
    return { schemaVersion: 1, suiteId: inputSuiteId, runId,
        scope: '39 standalone declared Avalonia events; paired complete state, independent raw local-child bytes and source-selected mode queries. No native keyboard, remote/mux transport, Windows runtime or stock-grade claim.',
        inputs: cases, initialSnapshot: 0, restoredSnapshot: 80, snapshots: frames, actions, seal, observations };
}
