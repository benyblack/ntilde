import { snapshotHash } from './agent-observer.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (ok, message) => { if (!ok) throw new Error('Core wrap geometry: ' + message); };
/** The two parent-owned actions must agree with the App and the same proved PTY.
 * Caller also validates the enclosing ownership receipt and captured frames. */
export function validateWrapGeometry({ runId, paneId, original, before, after, actions, frames }) {
    check(/^[a-f\d]{32}$/.test(runId ?? '') && /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(paneId ?? ''), 'invalid identity');
    check(Number.isSafeInteger(original?.rows) && original.rows >= 3 && Number.isSafeInteger(original?.cols)
        && original.cols >= 80 && original.cols <= 256, 'invalid original dimensions');
    check(before && after && same({ rows: before.rows, cols: before.cols }, original)
        && same({ rows: after.rows, cols: after.cols }, original) && snapshotHash(before) === snapshotHash(after), 'original state not restored');
    check(Array.isArray(actions) && actions.length === 2 && Array.isArray(frames) && frames.length > 0, 'missing actions or observations');
    const targets = [{ rows: original.rows, cols: 80 }, original];
    for (let i = 0; i < 2; i++) {
        const action = actions[i], observed = action?.observed, actual = observed?.app, target = targets[i];
        check(action?.index === i && same(action.target, target) && observed?.runId === runId && observed.paneId === paneId
            && typeof observed.observedAt === 'string' && Number.isFinite(Date.parse(observed.observedAt))
            && new Date(observed.observedAt).toISOString() === observed.observedAt
            && actual?.exact === true && actual.layoutSettled === true
            && same({ rows: actual.rows, cols: actual.cols }, target)
            && observed.terminalOutput?.tool === '/bin/stty' && observed.terminalOutput.outputProcessing === false, 'unverified resize action');
        validateOutputGeometry(observed.terminalOutput.description, target.rows, target.cols);
    }
    check(frames.every(frame => frame.paneId === paneId && frame.screen?.rows === original.rows && frame.screen.cols === 80), 'observation geometry mismatch');
}
