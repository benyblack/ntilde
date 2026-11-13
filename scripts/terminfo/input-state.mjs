import { snapshotHash, screenSnapshot } from './agent-observer.mjs';
import { defaultInputModes, inputAttemptCount, inputFrameCount, inputModeNumbers, inputModeQueries, inputPlanV1 } from './input-plan.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';
export { defaultInputModes, inputModeNumbers, inputModeQueries };
export const inputFinalRestore = '\x1b[?1049l\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[3J\x1b[H\x1b[?25h';
export const sameInputFact = (a, b) => snapshotHash(a) === snapshotHash(b);
export const checkInput = value => { if (!value) throw new Error('Invalid or incomplete owned routed-input evidence.'); };
export function boundInputScreen(initial, runId) {
    const bound = structuredClone(initial);
    bound.lines[0] = 'NTILDE_IN_' + runId; bound.cursorCol = bound.lines[0].length; bound.bufferState.alternateScreen = true;
    return bound;
}
function inputModeSet(number, modes) {
    if (number === 66) return modes.applicationKeypad;
    if (number === 1006) return modes.mouseSgr;
    return modes.mouseTracking === number;
}
export function inputModeReplyHex(modes) {
    const base = Buffer.from(inputModeNumbers.map(n => `\x1b[?${n};${inputModeSet(n, modes) ? 1 : 2}$y`).join('')
        + `\x1b[?${modes.kittyKeyboardFlags}u`).toString('hex');
    // v3 plans prove the negotiated modifyOtherKeys level through XTQMODKEYS; plans whose modes never carry the
    // level re-derive byte-for-byte as before.
    return modes.modifyOtherKeysLevel === undefined ? base
        : base + Buffer.from(`\x1b[>4;${modes.modifyOtherKeysLevel}m`).toString('hex');
}
/** Raw hex is a copied child observation; challenge bytes are never event bytes. */
export function validateInputBytes(value, expectedHex, used, exact = true) {
    checkInput(value && Object.keys(value).length === 4 && ['barrier', 'byteLength', 'hex', 'nonces'].every(key => Object.hasOwn(value, key))
        && typeof value.hex === 'string' && /^[a-f\d]*$/.test(value.hex) && value.hex.length % 2 === 0
        && value.byteLength === value.hex.length / 2 && value.byteLength <= 512 && (!exact || value.hex === expectedHex)
        && Array.isArray(value.nonces) && value.nonces.length === 2
        && value.nonces.every(n => Number.isSafeInteger(n) && n >= 32768 && n <= 65535) && value.nonces[0] !== value.nonces[1]
        && value.barrier === value.nonces.map(n => `\x1b[?${n};0$y`).join(''));
    const key = value.nonces.join(','); checkInput(!used.has(key)); used.add(key);
}
function matchesInputRequest(actual, event, paneId) {
    const expected = { paneId, ...event }, keys = Object.keys(expected);
    return actual !== null && typeof actual === 'object' && !Array.isArray(actual)
        && Object.keys(actual).length === keys.length
        && keys.every(key => Object.hasOwn(actual, key) && actual[key] === expected[key]);
}
export function validateInputAction(action, item, paneId, runId, geometry, profileName) {
    checkInput(action?.paneId === paneId && action.runId === runId && action.rpcStarted === true && action.postVerified === true
        && !action.error && matchesInputRequest(action.request, item.event, paneId)
        && typeof action.observedAt === 'string' && new Date(action.observedAt).toISOString() === action.observedAt);
    for (const side of ['before', 'after']) {
        const fact = action[side];
        checkInput(fact?.pane?.paneId === paneId && fact.pane.kind === 'local' && !fact.pane.windowless
            && typeof fact.pane.profileName === 'string' && fact.pane.profileName.length > 0 && fact.pane.profileName.length <= 128
            && (profileName === undefined || fact.pane.profileName === profileName)
            && fact.pane.rows === geometry.rows && fact.pane.cols === geometry.cols
            && fact.terminalOutput?.tool === '/bin/stty' && fact.terminalOutput.outputProcessing === false);
        checkInput(Number.isSafeInteger(fact.app?.pid) && fact.app.pid > 1 && Number.isSafeInteger(fact.app.uid) && fact.app.uid >= 0
            && typeof fact.app.executable === 'string' && fact.app.executable.startsWith('/')
            && Number.isSafeInteger(fact.worker?.pid) && fact.worker.pid > 1 && fact.worker.ppid === fact.app.pid
            && fact.worker.uid === fact.app.uid && /^tty[a-zA-Z0-9._-]+$/.test(fact.worker.tty ?? '')
            && typeof fact.worker.executable === 'string' && fact.worker.executable.startsWith('/')
            && typeof fact.appCommand === 'string' && fact.appCommand.split(/\s+/).includes(`--ntilde-terminfo-owner=${runId}`)
            && typeof fact.workerCommand === 'string' && fact.workerCommand.split(/\s+/).includes(`--ntilde-terminfo-worker=${runId}`));
        validateOutputGeometry(fact.terminalOutput.description, geometry.rows, geometry.cols);
    }
    checkInput(sameInputFact(action.before.app, action.after.app) && sameInputFact(action.before.worker, action.after.worker));
    const app = action.app;
    checkInput(app?.eventDispatched === true && app.observationAvailable === true && app.boundary === 'avalonia-routed'
        && typeof app.inputSubmitted === 'boolean' && typeof app.handled === 'boolean'
        && app.modifiers === item.event.modifiers && app.rows === geometry.rows && app.cols === geometry.cols
        && app.viewRows === geometry.rows && app.viewCols === geometry.cols
        && Object.entries(item.modes).every(([key, value]) => app[key] === value));
    // A key-down that carries text raised that TextInput right after it: its handled fact is retained evidence, absent for every other event.
    if (item.event.kind === 'keyDown' && item.event.text !== undefined) checkInput(typeof app.textHandled === 'boolean');
    else checkInput(app.textHandled == null);
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
export function buildInputReport(frames, actions, seal, runId, plan = inputPlanV1) {
    const count = Array.isArray(actions) ? actions.length : -1;
    checkInput(/^[a-f\d]{32}$/.test(runId ?? '') && frames?.length > 3 && count > 0 && frames.length === inputFrameCount(count));
    const paneId = frames[0].paneId, initial = frames[0].screen, geometry = { rows: initial.rows, cols: initial.cols };
    checkInput(pristineEditing(initial));
    const cases = plan.make(initial.rows, initial.cols), used = new Set(), profileName = actions[0]?.observed?.before?.pane?.profileName;
    checkInput(cases.length === count);
    const finalModes = plan.finalModes ?? defaultInputModes;
    const controlHex = item => (item.controlPrefixHex ?? '') + inputModeReplyHex(item.modes);
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
        validateInputBytes(control.capture, controlHex(item), used);
        validateInputBytes(observed.capture, item.expectedHex, used, plan.exactEvents);
        validateInputAction(actions[index].observed, item, paneId, runId, geometry, profileName);
        return { featureId: item.id, result: observed.capture.hex === item.expectedHex ? 'passed' : 'failed', expectedHex: item.expectedHex, actualHex: observed.capture.hex,
            controlSnapshot: control.index, snapshot: observed.index, actionIndex: index };
    });
    const final = frames.at(-1); validateInputFrame(final, frames.length - 1, 'input-finish', paneId);
    checkInput(sameInputFact(final.screen, initial)); validateInputBytes(final.capture, inputModeReplyHex(finalModes), used);
    checkInput(seal?.failed === false && seal.disposed === true && seal.attempts === inputAttemptCount(count) && seal.lastAttempt?.completed === true
        && seal.lastAttempt.error === null && seal.lastAttempt.fenceRequested === true
        && seal.lastAttempt.hex === final.capture.hex + Buffer.from(final.capture.barrier).toString('hex')
        && sameInputFact(seal.lastAttempt.nonces, final.capture.nonces));
    return { schemaVersion: plan.id === 'v1' ? 1 : plan.id === 'v2' ? 2 : 3, suiteId: plan.suiteId, runId, scope: plan.scope(count),
        inputs: cases, initialSnapshot: 0, restoredSnapshot: frames.length - 1, snapshots: frames, actions, seal, observations };
}
