import { screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { expandCellDetails } from './cell-details.mjs';
import { makeEditingCases, eraseSuiteId, editingSuiteId } from './editing-cases.mjs';
import { compressExpected, matchesExpected } from './editing-expectations.mjs';
import { pristineEditing } from './editing-capture.mjs';

const same = (left, right) => snapshotHash(left) === snapshotHash(right);
const reject = () => { throw new Error('Invalid or incomplete owned editing journal.'); };
const noncePrefix = prefix => prefix === 'erase' ? 'NTILDE_ER_' : 'NTILDE_EC_';
const suiteIdentity = prefix => prefix === 'erase' ? eraseSuiteId : editingSuiteId;
function frameKind(index, length, prefix) {
    if (index === 0) return prefix + '-start';
    if (index === 1) return prefix + '-bind';
    if (index === length - 1) return prefix + '-finish';
    return prefix + '-read';
}
function validateFrame(frame, index, frames, prefix, plan) {
    const canonical = screenSnapshot(frame.screen, false, true, true, true, true);
    const after = screenSnapshot(frame.afterScreen, false, true, true, true, true);
    const first = frames[0];
    if (frame.index !== index || frame.kind !== frameKind(index, frames.length, prefix)
        || frame.paneId !== first.paneId || !/^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(frame.paneId ?? '')
        || canonical.rows !== first.screen.rows || canonical.cols !== first.screen.cols
        || frame.sha256 !== snapshotHash(canonical) || frame.afterSha256 !== frame.sha256 || !same(canonical, after)
        || frame.caseId !== (plan.caseId ?? null) || frame.stageId !== (plan.stageId ?? null)
        || (index === 0 ? frame.barrier !== null : typeof frame.barrier !== 'string' || frame.barrier.length > 128 || !/^\x1b\[\?[\d;]+c$/.test(frame.barrier))) reject();
}
/** Validate ordering, nonce, all complete state/history brackets and exact restoration before grading. */
export function validateCellPlanFrames(frames, runId, { prefix, casesFor, nonce }) {
    if (!/^[a-z]{1,16}$/.test(prefix) || typeof casesFor !== 'function' || !/^[a-f\d]{32}$/.test(runId ?? '')
        || !Array.isArray(frames) || !frames.length || frames.length > 64) reject();
    const first = frames[0], last = frames.at(-1);
    const cases = casesFor(first.screen?.rows, first.screen?.cols).filter(value => value.id.startsWith(prefix + '.'));
    const plan = cases.flatMap(item => ['control', 'after'].map(stageId => ({ caseId: item.id, stageId })));
    if (frames.length !== plan.length + 3) reject();
    frames.forEach((frame, index) => validateFrame(frame, index, frames, prefix, plan[index - 2] ?? {}));
    if (!pristineEditing(first.screen) || !same(first.screen, last.screen)) reject();
    const bind = frames[1].screen;
    if (bind.lines[0] !== nonce + runId || bind.lines.slice(1).some(line => line !== '')
        || bind.cursorRow !== 0 || bind.cursorCol !== 42 || !bind.cursorVisible
        || !same(bind.bufferState, { ...first.screen.bufferState, alternateScreen: true })
        || !same(bind.cursorMode, first.screen.cursorMode) || !same(bind.cellDetails, first.screen.cellDetails)
        || !same(bind.history, first.screen.history)) reject();
    return cases;
}
function editingSpec(prefix) {
    if (!['erase', 'editing'].includes(prefix)) reject();
    return { prefix, casesFor: makeEditingCases, nonce: noncePrefix(prefix), suiteId: suiteIdentity(prefix),
        scope: 'Owned ASCII erase/editing stored cells, margins/modes/cursor and actual history text. BCE erased blanks qualify their literal background only; all attributes remain recorded. No rendered pixels, Unicode editing, full saved-state or stock grades.' };
}
export const validateEditingFrames = (frames, runId, prefix) => validateCellPlanFrames(frames, runId, editingSpec(prefix));
function comparison(frame, expected) {
    return matchesExpected(frame.screen, expected, expandCellDetails(frame.screen.cellDetails, frame.screen.rows * frame.screen.cols, true));
}
/** Supplemental stored-cell and history comparisons; malformed evidence never becomes an unsupported grade. */
export function buildCellPlanReport(frames, runId, spec) {
    const cases = validateCellPlanFrames(frames, runId, spec);
    const inputs = cases.map(item => {
        const value = { id: item.id, setup: item.setup, sequence: item.sequence,
            before: compressExpected(item.before), after: compressExpected(item.after) };
        return { ...value, sha256: snapshotHash(value) };
    });
    const observations = inputs.map((item, index) => {
        const before = frames[index * 2 + 2], after = frames[index * 2 + 3];
        const controls = [{ description: spec.controlDescription ?? 'known full viewport, cursor, modes and actual nonempty history before the operation', passed: comparison(before, item.before) }];
        const assertions = [{ description: 'exact expected text, cursor, margins/modes, history and every relevant cell attribute after the operation', passed: comparison(after, item.after) }];
        let result = 'passed';
        if (!controls.every(value => value.passed)) result = 'inconclusive';
        else if (!assertions.every(value => value.passed)) result = 'failed';
        return { featureId: item.id, snapshots: [before.index, after.index], controls, assertions, result };
    });
    return { schemaVersion: 1, suiteId: spec.suiteId, runId, complete: true,
        scope: spec.scope,
        inputs, initialSnapshot: 0, restoredSnapshot: frames.length - 1, snapshots: frames, observations };
}
export const buildEditingReport = (frames, runId, prefix) => buildCellPlanReport(frames, runId, editingSpec(prefix));
export const buildEraseReport = (frames, runId) => buildEditingReport(frames, runId, 'erase');
export const buildCellEditingReport = (frames, runId) => buildEditingReport(frames, runId, 'editing');
